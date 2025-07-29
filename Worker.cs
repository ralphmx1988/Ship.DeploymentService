using Docker.DotNet;
using Docker.DotNet.Models;
using Polly;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace Ship.DeploymentService;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly DockerClient _dockerClient;
    private readonly string _shipId;
    private readonly string _hqApiUrl;
    private readonly string _currentVersion;
    private readonly ResiliencePipeline _pullImagePipeline;
    private readonly ResiliencePipeline _httpPipeline;

    public Worker(ILogger<Worker> logger, IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _shipId = _configuration["ShipId"] ?? Environment.MachineName;
        _hqApiUrl = _configuration["HqApiUrl"] ?? "https://localhost:7001";

        // Initialize Docker client
        _dockerClient = new DockerClientConfiguration(new Uri("npipe://./pipe/docker_engine")).CreateClient();

        // Initialize Polly resilience pipelines
        _pullImagePipeline = CreatePullImagePipeline();
        _httpPipeline = CreateHttpPipeline();

        // Get current version from running container
        _currentVersion = GetCurrentVersionAsync().Result ?? "unknown";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Ship Deployment Service started for Ship: {ShipId}", _shipId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await HasInternetConnection())
                {
                    try
                    {
                        await SendHeartbeatAndCheckForUpdates();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error sending heartbeat after all retries exhausted");
                    }
                }
                else
                {
                    _logger.LogInformation("No internet connection, skipping update check");
                }

                // Check every 5 minutes
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in main service loop");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    private async Task<bool> HasInternetConnection()
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var response = await client.GetAsync(_hqApiUrl + "/api/ship");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task SendHeartbeatAndCheckForUpdates()
    {
        await _httpPipeline.ExecuteAsync(async (cancellationToken) =>
        {
            _logger.LogDebug("Executing heartbeat and update check");
            
            using var client = _httpClientFactory.CreateClient();

            var heartbeat = new
            {
                ShipId = _shipId,
                CurrentVersion = await GetCurrentVersionAsync() ?? "unknown",
                Timestamp = DateTime.UtcNow
            };

            var response = await client.PostAsJsonAsync($"{_hqApiUrl}/api/ship/{_shipId}/heartbeat", heartbeat, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<HeartbeatResponse>(responseContent, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (result?.PendingDeployments?.Any() == true)
                {
                    _logger.LogInformation("Found {Count} pending deployments", result.PendingDeployments.Count);

                    foreach (var deployment in result.PendingDeployments)
                    {
                        await ProcessDeployment(deployment);
                    }
                }
            }
            else
            {
                _logger.LogWarning("Heartbeat failed with status: {StatusCode}", response.StatusCode);
                // Throw exception to trigger retry for non-success status codes
                throw new HttpRequestException($"Heartbeat failed with status: {response.StatusCode}");
            }
        });
    }

    private async Task ProcessDeployment(Deployment deployment)
    {
        _logger.LogInformation("Processing deployment {DeploymentId} - {ImagePath}", deployment.Id, deployment.FullImagePath);

        try
        {
            // Pull the new image
            await PullImage(deployment.FullImagePath);

            // Update deployment status to Downloaded
            await UpdateDeploymentStatus(deployment.Id, "Downloaded");

            // Stop current container
            await StopContainer("employeemanagement");

            // Start new container with settings
            await StartContainerAsync(deployment);

            // Verify deployment
            await Task.Delay(10000); // Wait 10 seconds for container to start

            if (await IsContainerRunning("employeemanagement"))
            {
                await UpdateDeploymentStatus(deployment.Id, "Deployed");
                _logger.LogInformation("Deployment {DeploymentId} completed successfully", deployment.Id);
            }
            else
            {
                await UpdateDeploymentStatus(deployment.Id, "Failed", "Container failed to start");
                _logger.LogError("Deployment {DeploymentId} failed - container not running", deployment.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deployment {DeploymentId} failed", deployment.Id);
            try
            {
                await UpdateDeploymentStatus(deployment.Id, "Failed", ex.Message);
            }
            catch (Exception statusEx)
            {
                _logger.LogError(statusEx, "Failed to update deployment status for {DeploymentId} after deployment failure", deployment.Id);
            }
        }
    }

    private ResiliencePipeline CreatePullImagePipeline()
    {
        // Get retry configuration from settings with defaults
        var maxRetries = _configuration.GetValue<int>("DockerRegistry:PullRetryConfig:MaxRetries", 3);
        var baseDelayMs = _configuration.GetValue<int>("DockerRegistry:PullRetryConfig:BaseDelayMs", 2000);
        var maxDelayMs = _configuration.GetValue<int>("DockerRegistry:PullRetryConfig:MaxDelayMs", 30000);
        var timeoutMinutes = _configuration.GetValue<int>("DockerRegistry:PullRetryConfig:TimeoutMinutes", 10);

        return new ResiliencePipelineBuilder()
            .AddRetry(new Polly.Retry.RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
                    .Handle<SocketException>()
                    .Handle<Exception>(ex => 
                        ex.Message?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true ||
                        ex.Message?.Contains("network", StringComparison.OrdinalIgnoreCase) == true ||
                        ex.Message?.Contains("connection", StringComparison.OrdinalIgnoreCase) == true ||
                        ex.Message?.Contains("unreachable", StringComparison.OrdinalIgnoreCase) == true),
                MaxRetryAttempts = maxRetries,
                Delay = TimeSpan.FromMilliseconds(baseDelayMs),
                UseJitter = true,
                MaxDelay = TimeSpan.FromMilliseconds(maxDelayMs),
                OnRetry = args =>
                {
                    _logger.LogWarning("Retry attempt {AttemptNumber} for image pull. Delay: {Delay}ms. Exception: {Exception}",
                        args.AttemptNumber, args.RetryDelay.TotalMilliseconds, args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(TimeSpan.FromMinutes(timeoutMinutes))
            .Build();
    }

    private ResiliencePipeline CreateHttpPipeline()
    {
        // Get HTTP retry configuration from settings with defaults
        var maxRetries = _configuration.GetValue<int>("HttpClient:RetryConfig:MaxRetries", 3);
        var baseDelayMs = _configuration.GetValue<int>("HttpClient:RetryConfig:BaseDelayMs", 1000);
        var maxDelayMs = _configuration.GetValue<int>("HttpClient:RetryConfig:MaxDelayMs", 10000);
        var timeoutSeconds = _configuration.GetValue<int>("HttpClient:RetryConfig:TimeoutSeconds", 30);

        return new ResiliencePipelineBuilder()
            .AddRetry(new Polly.Retry.RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
                    .Handle<SocketException>()
                    .Handle<Exception>(ex => 
                        ex.Message?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true ||
                        ex.Message?.Contains("network", StringComparison.OrdinalIgnoreCase) == true ||
                        ex.Message?.Contains("connection", StringComparison.OrdinalIgnoreCase) == true ||
                        ex.Message?.Contains("unreachable", StringComparison.OrdinalIgnoreCase) == true),
                MaxRetryAttempts = maxRetries,
                Delay = TimeSpan.FromMilliseconds(baseDelayMs),
                UseJitter = true,
                MaxDelay = TimeSpan.FromMilliseconds(maxDelayMs),
                OnRetry = args =>
                {
                    var exception = args.Outcome.Exception?.Message ?? "HTTP communication error";
                    _logger.LogWarning("HTTP retry attempt {AttemptNumber}. Delay: {Delay}ms. Reason: {Exception}",
                        args.AttemptNumber, args.RetryDelay.TotalMilliseconds, exception);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(TimeSpan.FromSeconds(timeoutSeconds))
            .Build();
    }

    private async Task PullImage(string imagePath)
    {
        _logger.LogInformation("Pulling image: {ImagePath}", imagePath);

        AuthConfig auth = new AuthConfig
        {
            Username = _configuration["DockerRegistry:Username"],
            Password = _configuration["DockerRegistry:Password"]
        };

        await _pullImagePipeline.ExecuteAsync(async (cancellationToken) =>
        {
            _logger.LogDebug("Executing image pull for: {ImagePath}", imagePath);
            
            await _dockerClient.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = imagePath },
                auth,
                new Progress<JSONMessage>(msg => _logger.LogDebug("Pull: {Status}", msg.Status)),
                cancellationToken);

            _logger.LogInformation("Successfully pulled image: {ImagePath}", imagePath);
        });
    }

    private async Task StopContainer(string containerName)
    {
        try
        {
            var containers = await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["name"] = new Dictionary<string, bool> { [containerName] = true }
                }
            });

            var container = containers.FirstOrDefault();
            if (container != null)
            {
                _logger.LogInformation("Stopping container: {ContainerName}", containerName);

                if (container.State == "running")
                {
                    await _dockerClient.Containers.StopContainerAsync(container.ID, new ContainerStopParameters
                    {
                        WaitBeforeKillSeconds = 30
                    });
                }

                await _dockerClient.Containers.RemoveContainerAsync(container.ID, new ContainerRemoveParameters
                {
                    Force = true
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping container {ContainerName}", containerName);
        }
    }

    public async Task<string> StartContainerAsync(Deployment deployment)
    {
        _logger.LogInformation("Starting container with image: {ImagePath}", deployment.FullImagePath);

        try
        {
            var envVars = deployment.Settings?.Select(kv => $"{kv.Key}={kv.Value}").ToList() ?? new List<string>();

            
            var defaultEnvVars = new List<string>
        {
            "ASPNETCORE_ENVIRONMENT=Production",
            "ASPNETCORE_URLS=http://+:80",
            "DOTNET_RUNNING_IN_CONTAINER=true",
            $"DEPLOYMENT_ID={deployment.Id}",
            $"DEPLOYMENT_VERSION={deployment.ImageTag}",
            $"DEPLOYMENT_TIMESTAMP={DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC",
            $"DEPLOYED_BY=ralphmx1988"
        };

            var allEnvVars = defaultEnvVars.Concat(envVars).ToList();

            var createParams = new CreateContainerParameters
            {
                Image = deployment.FullImagePath,
                Name = "employeemanagement",
                Env = allEnvVars,
                ExposedPorts = new Dictionary<string, EmptyStruct>
            {
                { "80/tcp", new EmptyStruct() }

            },
                HostConfig = new HostConfig
                {
                    PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    { "80/tcp", new List<PortBinding> { new() { HostPort = "8080" } } }
                },
                    RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                    Memory = 2147483648,
                    CPUCount = 2,
                    Mounts = new List<Mount>
                {
                    new Mount
                    {
                        Type = "bind",
                        Source = "C:\\EmployeeData",
                        Target = "/app/data",
                        ReadOnly = false
                    }
                }
                },
                Labels = new Dictionary<string, string>
                {
                    ["deployment.id"] = deployment.Id,
                    ["deployment.version"] = deployment.ImageTag,
                    ["deployment.ship"] = _shipId,
                    ["deployment.timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
                    ["deployment.user"] = "ralphmx1988",
                    ["app.name"] = "EmployeeManagement",
                    ["app.environment"] = "Production"
                },
                WorkingDir = "/app",
                Healthcheck = new HealthConfig
                {
                    Test = new List<string> { "CMD-SHELL", "curl -f http://localhost:80/health || exit 1" },
                    Interval = TimeSpan.FromSeconds(30),
                    Timeout = TimeSpan.FromSeconds(10),
                    Retries = 3,
                    StartPeriod = 30_000_000_000L,
                }
            };

            _logger.LogInformation("Creating container with {EnvCount} environment variables", allEnvVars.Count);

            var response = await _dockerClient.Containers.CreateContainerAsync(createParams);

            _logger.LogInformation("Container created with ID: {ContainerId}", response.ID[..12]);

            await _dockerClient.Containers.StartContainerAsync(response.ID, new ContainerStartParameters());

            _logger.LogInformation("Container started successfully with ID: {ContainerId}", response.ID[..12]);

           
            _logger.LogDebug("Container environment variables: {EnvVars}", string.Join(", ", allEnvVars));

            return response.ID;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start container with image: {ImagePath}", deployment.FullImagePath);
            throw;
        }
    }

    private async Task<bool> IsContainerRunning(string containerName)
    {
        try
        {
            var containers = await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["name"] = new Dictionary<string, bool> { [containerName] = true }
                }
            });

            return containers.Any(c => c.State == "running");
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> GetCurrentVersionAsync()
    {
        try
        {
            var containers = await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["name"] = new Dictionary<string, bool> { ["employeemanagement"] = true }
                }
            });

            var container = containers.FirstOrDefault();
            if (container != null && container.Labels.TryGetValue("deployment.version", out var version))
            {
                return version;
            }

            // Fallback: extract from image name
            return container?.Image?.Split(':').LastOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private async Task UpdateDeploymentStatus(string deploymentId, string status, string? errorMessage = null)
    {
        await _httpPipeline.ExecuteAsync(async (cancellationToken) =>
        {
            _logger.LogDebug("Updating deployment status for {DeploymentId} to {Status}", deploymentId, status);
            
            using var client = _httpClientFactory.CreateClient();

            var statusUpdate = new
            {
                Status = Enum.Parse<DeploymentStatus>(status),
                ErrorMessage = errorMessage
            };

            var response = await client.PutAsJsonAsync($"{_hqApiUrl}/api/ship/deployment/{deploymentId}/status", statusUpdate, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Failed to update deployment status. Status: {response.StatusCode}");
            }
            
            _logger.LogDebug("Successfully updated deployment status for {DeploymentId}", deploymentId);
        });
    }
}

// Supporting classes
public class HeartbeatResponse
{
    public string Message { get; set; } = string.Empty;
    public List<Deployment> PendingDeployments { get; set; } = new();
}

public class Deployment
{
    public string Id { get; set; } = string.Empty;
    public string ShipId { get; set; } = string.Empty;
    public string ImageName { get; set; } = string.Empty;
    public string ImageTag { get; set; } = string.Empty;
    public string FullImagePath { get; set; } = string.Empty;
    public Dictionary<string, string> Settings { get; set; } = new();
}

public enum DeploymentStatus
{
    Pending,
    Downloaded,
    Deployed,
    Failed
}
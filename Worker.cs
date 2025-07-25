using Docker.DotNet;
using Docker.DotNet.Models;
using System.Net.Http.Json;
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

    public Worker(ILogger<Worker> logger, IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _shipId = _configuration["ShipId"] ?? Environment.MachineName;
        _hqApiUrl = _configuration["HqApiUrl"] ?? "https://localhost:7001";

        // Initialize Docker client
        _dockerClient = new DockerClientConfiguration(new Uri("npipe://./pipe/docker_engine")).CreateClient();

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
                    await SendHeartbeatAndCheckForUpdates();
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
        try
        {
            using var client = _httpClientFactory.CreateClient();

            var heartbeat = new
            {
                ShipId = _shipId,
                CurrentVersion = await GetCurrentVersionAsync() ?? "unknown",
                Timestamp = DateTime.UtcNow
            };

            var response = await client.PostAsJsonAsync($"{_hqApiUrl}/api/ship/{_shipId}/heartbeat", heartbeat);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
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
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending heartbeat");
        }
    }

    private async Task ProcessDeployment(Deployment deployment)
    {
        _logger.LogInformation("Processing deployment {DeploymentId} - {ImagePath}", deployment.Id, deployment.FullImagePath);

        try
        {
            // Update deployment status to Downloaded
            await UpdateDeploymentStatus(deployment.Id, "Downloaded");

            // Pull the new image
            await PullImage(deployment.FullImagePath);

            // Stop current container
            await StopContainer("employeemanagement");

            // Start new container with settings
            await StartContainer(deployment);

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
            await UpdateDeploymentStatus(deployment.Id, "Failed", ex.Message);
        }
    }

    private async Task PullImage(string imagePath)
    {
        _logger.LogInformation("Pulling image: {ImagePath}", imagePath);

        await _dockerClient.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = imagePath },
            new AuthConfig(), // Add ACR credentials if needed
            new Progress<JSONMessage>(msg => _logger.LogDebug("Pull: {Status}", msg.Status)));
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

    private async Task StartContainer(Deployment deployment)
    {
        _logger.LogInformation("Starting container with image: {ImagePath}", deployment.FullImagePath);

        var envVars = deployment.Settings.Select(kv => $"{kv.Key}={kv.Value}").ToList();

        var createParams = new CreateContainerParameters
        {
            Image = deployment.FullImagePath,
            Name = "employeemanagement",
            Env = envVars,
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
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped }
            },
            Labels = new Dictionary<string, string>
            {
                ["deployment.id"] = deployment.Id,
                ["deployment.version"] = deployment.ImageTag,
                ["deployment.ship"] = _shipId
            }
        };

        var response = await _dockerClient.Containers.CreateContainerAsync(createParams);
        await _dockerClient.Containers.StartContainerAsync(response.ID, new ContainerStartParameters());

        _logger.LogInformation("Container started with ID: {ContainerId}", response.ID);
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
        try
        {
            using var client = _httpClientFactory.CreateClient();

            var statusUpdate = new
            {
                Status = Enum.Parse<DeploymentStatus>(status),
                ErrorMessage = errorMessage
            };

            await client.PutAsJsonAsync($"{_hqApiUrl}/api/ship/deployment/{deploymentId}/status", statusUpdate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update deployment status");
        }
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
# Installation Prerequisites & Software Requirements

This document outlines all the necessary software, tools, and configurations required to install and run the Ship Deployment Service on a Windows environment.

## 🖥️ System Requirements

### Minimum Hardware Requirements
- **CPU**: 2 cores (4 cores recommended)
- **RAM**: 4 GB (8 GB recommended)
- **Storage**: 20 GB free space (50 GB recommended for container images)
- **Network**: Stable internet connection for container registry access

### Operating System Requirements
- **Windows 10 Pro/Enterprise** (version 1903 or later)
- **Windows 11 Pro/Enterprise**
- **Windows Server 2019** or later
- **Windows Server Core** supported

> ⚠️ **Note**: Windows Home editions are not supported due to Docker Desktop limitations.

## 🛠️ Required Software Installation

### 1. .NET 9.0 SDK

**Purpose**: Runtime environment for the Ship Deployment Service

**Installation Steps**:
```powershell
# Download and install .NET 9.0 SDK
# Visit: https://dotnet.microsoft.com/download/dotnet/9.0

# Verify installation
dotnet --version
# Expected output: 9.0.x
```

**Alternative Installation via Winget**:
```powershell
winget install Microsoft.DotNet.SDK.9
```

**Alternative Installation via Chocolatey**:
```powershell
choco install dotnet-9.0-sdk
```

### 2. Docker Desktop for Windows

**Purpose**: Container runtime for managing Docker containers

**Installation Steps**:
1. Download Docker Desktop from: https://www.docker.com/products/docker-desktop/
2. Run the installer with Administrator privileges
3. Enable WSL 2 backend during installation
4. Restart the computer when prompted

**Configuration**:
```powershell
# Verify Docker installation
docker --version
docker-compose --version

# Test Docker functionality
docker run hello-world
```

**Alternative Installation via Winget**:
```powershell
winget install Docker.DockerDesktop
```

**Alternative Installation via Chocolatey**:
```powershell
choco install docker-desktop
```

### 3. Windows Subsystem for Linux 2 (WSL 2)

**Purpose**: Required by Docker Desktop for optimal performance

**Installation Steps**:
```powershell
# Enable WSL feature
dism.exe /online /enable-feature /featurename:Microsoft-Windows-Subsystem-Linux /all /norestart

# Enable Virtual Machine Platform
dism.exe /online /enable-feature /featurename:VirtualMachinePlatform /all /norestart

# Restart computer
Restart-Computer

# Set WSL 2 as default version
wsl --set-default-version 2

# Install Ubuntu (recommended)
wsl --install -d Ubuntu
```

### 4. Git for Windows

**Purpose**: Version control and source code management

**Installation Steps**:
```powershell
# Download from: https://git-scm.com/download/win
# Or install via Winget
winget install Git.Git

# Verify installation
git --version
```

### 5. PowerShell 7+ (Optional but Recommended)

**Purpose**: Enhanced shell experience and better script compatibility

**Installation Steps**:
```powershell
# Install via Winget
winget install Microsoft.PowerShell

# Or via MSI from: https://github.com/PowerShell/PowerShell/releases
```

## 🔧 Development Tools (Optional)

### Visual Studio Code

**Purpose**: Code editing and debugging

```powershell
# Install via Winget
winget install Microsoft.VisualStudioCode

# Recommended extensions:
# - C# Dev Kit
# - Docker
# - GitLens
# - REST Client
```

### Visual Studio 2022 Community/Professional

**Purpose**: Full IDE with debugging capabilities

```powershell
# Install via Winget
winget install Microsoft.VisualStudio.2022.Community

# Required workloads:
# - .NET Desktop Development
# - ASP.NET and web development
```

## 🌐 Network & Connectivity Requirements

### Firewall Configuration

**Required Ports**:
```
Outbound:
- Port 443 (HTTPS) - HQ Server communication
- Port 443 (HTTPS) - Container registry access
- Port 80/443 - General internet access

Inbound:
- Port 8080 - Application access (configurable)
- Port 2376 - Docker daemon (if remote access needed)
```

**Windows Firewall Rules**:
```powershell
# Allow Docker Desktop
New-NetFirewallRule -DisplayName "Docker Desktop" -Direction Inbound -Program "C:\Program Files\Docker\Docker\Docker Desktop.exe" -Action Allow

# Allow .NET application
New-NetFirewallRule -DisplayName "Ship Deployment Service" -Direction Inbound -Program "C:\Path\To\Ship.DeploymentService.exe" -Action Allow
```

### DNS Configuration

**Required Domain Access**:
- `docker.io` - Docker Hub registry
- `*.azurecr.io` - Azure Container Registry
- Your HQ server domain
- `api.nuget.org` - NuGet packages

## 🏗️ Container Registry Access

### Azure Container Registry Setup

**Prerequisites**:
- Valid Azure subscription
- Azure CLI (optional for management)

**Azure CLI Installation** (Optional):
```powershell
# Install via Winget
winget install Microsoft.AzureCLI

# Login to Azure
az login

# Get registry credentials
az acr credential show --name yourregistryname
```

### Registry Authentication

**Configuration in appsettings.json**:
```json
{
  "DockerRegistry": {
    "Username": "your-registry-username",
    "Password": "your-registry-password"
  }
}
```

## 🔐 Security & Permissions

### Required User Permissions

**Windows Service Installation**:
- **Administrator privileges** for service installation
- **Local Service Account** for service execution

**Docker Permissions**:
- User must be member of **"docker-users"** group
- **Hyper-V Administrators** group (for Windows containers)

**File System Permissions**:
```powershell
# Grant permissions to service account
$acl = Get-Acl "C:\Ship.DeploymentService"
$accessRule = New-Object System.Security.AccessControl.FileSystemAccessRule("NT SERVICE\ShipDeploymentService","FullControl","ContainerInherit,ObjectInherit","None","Allow")
$acl.SetAccessRule($accessRule)
Set-Acl "C:\Ship.DeploymentService" $acl
```

## 📁 Directory Structure Setup

### Required Directories

```powershell
# Create application directory
New-Item -Path "C:\Ship.DeploymentService" -ItemType Directory -Force

# Create log directory
New-Item -Path "C:\ShipLogs" -ItemType Directory -Force

# Create data directory (for container mounts)
New-Item -Path "C:\EmployeeData" -ItemType Directory -Force

# Set permissions
icacls "C:\ShipLogs" /grant "NT SERVICE\ShipDeploymentService:(OI)(CI)F"
icacls "C:\EmployeeData" /grant "NT SERVICE\ShipDeploymentService:(OI)(CI)F"
```

## 🔧 Environment Configuration

### Environment Variables

**System Environment Variables**:
```powershell
# Set system-wide environment variables
[Environment]::SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production", "Machine")
[Environment]::SetEnvironmentVariable("SHIP_ID", "SHIP-001", "Machine")
[Environment]::SetEnvironmentVariable("HQ_API_URL", "https://your-hq-server.com", "Machine")
```

**User Secrets** (Development):
```powershell
# Navigate to project directory
cd "C:\Ship.DeploymentService"

# Initialize user secrets
dotnet user-secrets init

# Set sensitive configuration
dotnet user-secrets set "DockerRegistry:Username" "your-username"
dotnet user-secrets set "DockerRegistry:Password" "your-password"
```

## 📋 Installation Verification Checklist

### Pre-Installation Verification

```powershell
# Check .NET installation
dotnet --version
# Expected: 9.0.x

# Check Docker installation
docker --version
docker ps
# Should list running containers (may be empty)

# Check Docker daemon
docker info
# Should show system information

# Check network connectivity
Test-NetConnection -ComputerName "docker.io" -Port 443
Test-NetConnection -ComputerName "your-hq-server.com" -Port 443

# Check WSL 2
wsl --list --verbose
# Should show WSL 2 distributions
```

### Post-Installation Verification

```powershell
# Verify service installation
Get-Service "ShipDeploymentService"

# Check service logs
Get-Content "C:\ShipLogs\ship-service-*.log" -Tail 20

# Test container functionality
docker run --rm hello-world

# Verify application endpoints
Invoke-RestMethod -Uri "http://localhost:8080/health" -Method Get
```

## 🚨 Troubleshooting Common Issues

### Docker Issues

**Docker Desktop won't start**:
```powershell
# Check Hyper-V status
Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V

# Enable if disabled
Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V -All
```

**Permission denied errors**:
```powershell
# Add user to docker-users group
Add-LocalGroupMember -Group "docker-users" -Member $env:USERNAME

# Restart Docker Desktop
Restart-Service "com.docker.service"
```

### .NET Issues

**Assembly loading errors**:
```powershell
# Clear NuGet cache
dotnet nuget locals all --clear

# Restore packages
dotnet restore

# Rebuild solution
dotnet build --no-restore
```

### Network Issues

**Cannot reach container registry**:
```powershell
# Test DNS resolution
Resolve-DnsName "yourregistry.azurecr.io"

# Test proxy settings
netsh winhttp show proxy

# Configure Docker proxy (if needed)
# Add to Docker Desktop settings
```

## 📞 Support Resources

### Official Documentation
- **.NET 9.0**: https://docs.microsoft.com/en-us/dotnet/
- **Docker Desktop**: https://docs.docker.com/desktop/windows/
- **WSL 2**: https://docs.microsoft.com/en-us/windows/wsl/

### Community Support
- **Docker Community**: https://forums.docker.com/
- **.NET Community**: https://dotnet.microsoft.com/platform/community

### System Administration
- **Windows Admin Center**: For server management
- **PowerShell Documentation**: For scripting automation

---

## 📋 Quick Installation Script

For automated installation, use this PowerShell script:

```powershell
# Quick installation script
# Run as Administrator

# Install Winget if not present
if (!(Get-Command winget -ErrorAction SilentlyContinue)) {
    Write-Host "Installing Winget..."
    # Install winget from Microsoft Store or GitHub
}

# Install required software
winget install Microsoft.DotNet.SDK.9
winget install Docker.DockerDesktop
winget install Git.Git
winget install Microsoft.PowerShell

# Enable WSL 2
wsl --install

# Create directories
New-Item -Path "C:\Ship.DeploymentService", "C:\ShipLogs", "C:\EmployeeData" -ItemType Directory -Force

Write-Host "Installation complete! Please restart your computer and configure Docker Desktop."
```

**Save this script as `install-prerequisites.ps1` and run with Administrator privileges.**

---

*This installation guide ensures a complete and properly configured environment for the Ship Deployment Service.*

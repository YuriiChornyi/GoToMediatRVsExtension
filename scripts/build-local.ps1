# Local Build Script for MediatR Navigation Extension
# This script mimics the GitHub Actions build process for local testing

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    
    [Parameter(Mandatory=$false)]
    [switch]$Clean,
    
    [Parameter(Mandatory=$false)]
    [switch]$Test,
    
    [Parameter(Mandatory=$false)]
    [switch]$Install
)

# Set error handling
$ErrorActionPreference = "Stop"

# Get script directory
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = Split-Path -Parent $ScriptDir
Set-Location $RootDir

Write-Host "=== MediatR Navigation Extension - Local Build ===" -ForegroundColor Cyan
Write-Host "Configuration: $Configuration" -ForegroundColor Yellow
Write-Host "Root Directory: $RootDir" -ForegroundColor Yellow
Write-Host ""

try {
    # Clean if requested
    if ($Clean) {
        Write-Host "🧹 Cleaning previous builds..." -ForegroundColor Blue
        
        if (Test-Path "bin") { Remove-Item "bin" -Recurse -Force }
        if (Test-Path "obj") { Remove-Item "obj" -Recurse -Force }
        
        # Clean project-specific directories
        Get-ChildItem -Path . -Directory | ForEach-Object {
            $binPath = Join-Path $_.FullName "bin"
            $objPath = Join-Path $_.FullName "obj"
            if (Test-Path $binPath) { Remove-Item $binPath -Recurse -Force }
            if (Test-Path $objPath) { Remove-Item $objPath -Recurse -Force }
        }
        
        Write-Host "✅ Clean completed" -ForegroundColor Green
        Write-Host ""
    }
    
    # Check prerequisites
    Write-Host "🔍 Checking prerequisites..." -ForegroundColor Blue
    
    # Check MSBuild
    $msbuild = Get-Command "msbuild" -ErrorAction SilentlyContinue
    if (-not $msbuild) {
        Write-Error "MSBuild not found. Please install Visual Studio or Build Tools."
    }
    Write-Host "  ✅ MSBuild: $($msbuild.Version)" -ForegroundColor Green
    
    # Check NuGet
    $nuget = Get-Command "nuget" -ErrorAction SilentlyContinue
    if (-not $nuget) {
        Write-Host "  ⚠️  NuGet CLI not found, trying to download..." -ForegroundColor Yellow
        $nugetUrl = "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe"
        $nugetPath = Join-Path $env:TEMP "nuget.exe"
        Invoke-WebRequest -Uri $nugetUrl -OutFile $nugetPath
        $nuget = Get-Command $nugetPath
    }
    Write-Host "  ✅ NuGet: $($nuget.Version)" -ForegroundColor Green
    
    Write-Host ""

    # Run unit tests (same suite the CI workflows run)
    if ($Test) {
        Write-Host "🧪 Running unit tests..." -ForegroundColor Blue

        & dotnet test "Tests\VSIXExtension.Tests\VSIXExtension.Tests.csproj" --configuration $Configuration --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "Unit tests failed"
        }

        Write-Host "✅ Unit tests passed" -ForegroundColor Green
        Write-Host ""
    }

    # Restore NuGet packages
    Write-Host "📦 Restoring NuGet packages..." -ForegroundColor Blue
    & $nuget.Source restore "VSIXExtention.sln"
    if ($LASTEXITCODE -ne 0) {
        throw "NuGet restore failed"
    }
    Write-Host "✅ NuGet restore completed" -ForegroundColor Green
    Write-Host ""
    
    # Get current version
    Write-Host "📋 Reading version information..." -ForegroundColor Blue
    [xml]$manifest = Get-Content "source.extension.vsixmanifest"
    $version = $manifest.PackageManifest.Metadata.Identity.Version
    $displayName = $manifest.PackageManifest.Metadata.DisplayName
    Write-Host "  Extension: $displayName" -ForegroundColor Green
    Write-Host "  Version: $version" -ForegroundColor Green
    Write-Host ""
    
    # Build solution
    Write-Host "🔨 Building solution..." -ForegroundColor Blue
    $buildArgs = @(
        "VSIXExtention.sln"
        "/p:Configuration=$Configuration"
        "/p:Platform=Any CPU"
        "/p:DeployExtension=false"
        "/p:ZipPackageCompressionLevel=normal"
        "/v:minimal"
    )
    
    & $msbuild.Source $buildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed"
    }
    
    Write-Host "✅ Build completed successfully" -ForegroundColor Green
    Write-Host ""
    
    # Find VSIX file
    Write-Host "🔍 Locating VSIX file..." -ForegroundColor Blue
    $vsixFile = Get-ChildItem -Path "bin\$Configuration" -Filter "*.vsix" -Recurse | Select-Object -First 1
    
    if (-not $vsixFile) {
        throw "VSIX file not found in bin\$Configuration"
    }
    
    $vsixPath = $vsixFile.FullName
    $vsixSize = [math]::Round($vsixFile.Length / 1KB, 2)
    Write-Host "  ✅ VSIX: $($vsixFile.Name) ($vsixSize KB)" -ForegroundColor Green
    Write-Host "  📁 Path: $vsixPath" -ForegroundColor Green
    Write-Host ""
    
    # Test VSIX integrity
    if ($Test) {
        Write-Host "🧪 Testing VSIX integrity..." -ForegroundColor Blue
        
        try {
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $zip = [System.IO.Compression.ZipFile]::OpenRead($vsixPath)
            $entryCount = $zip.Entries.Count
            $zip.Dispose()
            
            Write-Host "  ✅ VSIX contains $entryCount entries" -ForegroundColor Green
            
            # Check for required files
            $zip = [System.IO.Compression.ZipFile]::OpenRead($vsixPath)
            $hasManifest = $zip.Entries | Where-Object { $_.Name -eq "extension.vsixmanifest" }
            $hasDll = $zip.Entries | Where-Object { $_.Name -like "*.dll" }
            $zip.Dispose()
            
            if ($hasManifest) {
                Write-Host "  ✅ Contains extension manifest" -ForegroundColor Green
            } else {
                Write-Host "  ⚠️  Missing extension manifest" -ForegroundColor Yellow
            }
            
            if ($hasDll) {
                Write-Host "  ✅ Contains extension assembly" -ForegroundColor Green
            } else {
                Write-Host "  ⚠️  Missing extension assembly" -ForegroundColor Yellow
            }
            
        } catch {
            Write-Host "  ❌ VSIX integrity test failed: $($_.Exception.Message)" -ForegroundColor Red
        }
        
        Write-Host ""
    }
    
    # Install VSIX
    if ($Install) {
        Write-Host "🚀 Installing VSIX..." -ForegroundColor Blue
        
        # Find Visual Studio instances
        $vsInstances = @()
        
        # Try to find VS 2022
        $vs2022Paths = @(
            "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\VSIXInstaller.exe",
            "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\Common7\IDE\VSIXInstaller.exe",
            "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\Common7\IDE\VSIXInstaller.exe"
        )
        
        $vsixInstaller = $vs2022Paths | Where-Object { Test-Path $_ } | Select-Object -First 1
        
        if ($vsixInstaller) {
            Write-Host "  📍 Found VS 2022 installer: $vsixInstaller" -ForegroundColor Green
            
            # Install VSIX
            $installArgs = @("/quiet", $vsixPath)
            & $vsixInstaller $installArgs
            
            if ($LASTEXITCODE -eq 0) {
                Write-Host "  ✅ VSIX installed successfully!" -ForegroundColor Green
                Write-Host "  🔄 Please restart Visual Studio to use the extension" -ForegroundColor Yellow
            } else {
                Write-Host "  ⚠️  VSIX installation may have failed (exit code: $LASTEXITCODE)" -ForegroundColor Yellow
            }
        } else {
            Write-Host "  ❌ Visual Studio 2022 not found" -ForegroundColor Red
            Write-Host "  📁 You can manually install: $vsixPath" -ForegroundColor Yellow
        }
        
        Write-Host ""
    }
    
    # Success summary
    Write-Host "🎉 Build Summary" -ForegroundColor Cyan
    Write-Host "  Extension: $displayName v$version" -ForegroundColor White
    Write-Host "  Configuration: $Configuration" -ForegroundColor White
    Write-Host "  VSIX File: $($vsixFile.Name)" -ForegroundColor White
    Write-Host "  Size: $vsixSize KB" -ForegroundColor White
    Write-Host "  Location: $vsixPath" -ForegroundColor White
    Write-Host ""
    Write-Host "✅ Build completed successfully! 🚀" -ForegroundColor Green
    
} catch {
    Write-Host ""
    Write-Host "❌ Build failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    exit 1
}

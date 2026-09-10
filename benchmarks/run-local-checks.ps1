param([string]$AssemblyPath)
$ErrorActionPreference = 'Stop'
$CoreRoot = Split-Path $PSScriptRoot -Parent
$checkRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('versa-check-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $checkRoot | Out-Null
if (-not $AssemblyPath) {
    dotnet build (Join-Path $CoreRoot 'versa-core.csproj') --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Core build failed.' }
    $AssemblyPath = Join-Path $CoreRoot 'bin/Debug/net10.0/versa-core.dll'
}
$assemblyDirectory = Split-Path (Resolve-Path -LiteralPath $AssemblyPath).Path

@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><Reference Include="$assemblyDirectory/*.dll" /></ItemGroup></Project>
"@ | Set-Content (Join-Path $checkRoot 'checks.csproj')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'check-intrinsic.cs.txt') -Destination (Join-Path $checkRoot 'Program.cs')
# The reflection harness references DLLs directly, so MSBuild does not copy the
# Playwright driver content transitively as a PackageReference would.
New-Item -ItemType Directory -Path (Join-Path $checkRoot 'bin') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $assemblyDirectory '.playwright') -Destination (Join-Path $checkRoot 'bin/.playwright') -Recurse
dotnet run --project (Join-Path $checkRoot 'checks.csproj')
if ($LASTEXITCODE -ne 0) { throw 'Local intrinsic checks failed.' }
& (Join-Path $PSScriptRoot 'check-writing-expectations.ps1')
& (Join-Path $PSScriptRoot 'check-recurrence.ps1')

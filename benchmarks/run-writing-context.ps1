param([string]$AssemblyPath, [string]$CasesPath, [string]$ResultsPath, [int]$Rounds = 3)
$ErrorActionPreference='Stop'
if (-not [IO.Path]::IsPathRooted($ResultsPath) -and -not [IO.Path]::GetDirectoryName($ResultsPath)) { $ResultsPath = Join-Path $PSScriptRoot ('results/' + $ResultsPath) }
New-Item -ItemType Directory -Path (Split-Path ([IO.Path]::GetFullPath($ResultsPath))) -Force | Out-Null
$assembly = (Resolve-Path -LiteralPath $AssemblyPath).Path
$assemblyDirectory = Split-Path $assembly
$contextRoot = Join-Path ([IO.Path]::GetTempPath()) ('versa-context-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $contextRoot | Out-Null
@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><Reference Include="$assemblyDirectory/*.dll" /></ItemGroup></Project>
"@ | Set-Content (Join-Path $contextRoot 'context.csproj')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'run-writing-context.cs.txt') -Destination (Join-Path $contextRoot 'Program.cs')
dotnet run --project (Join-Path $contextRoot 'context.csproj') -- $assembly (Resolve-Path -LiteralPath $CasesPath).Path ([IO.Path]::GetFullPath($ResultsPath)) $Rounds
if ($LASTEXITCODE -ne 0) { throw 'Context experiment failed.' }

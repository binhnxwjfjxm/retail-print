$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'RetailPrint\RetailPrint.csproj'
$output = Join-Path $root 'publish\win-x64'

Write-Host 'Building Retail Print...'
dotnet restore $project
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o $output

Write-Host "Done: $output"

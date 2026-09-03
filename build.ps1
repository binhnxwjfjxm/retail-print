$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'RetailPrint\RetailPrint.csproj'
$output = Join-Path $root 'publish\win-x64'
$installerScript = Join-Path $root 'installer\RetailPrint.iss'
$installerOutput = Join-Path $root 'publish\installer\RetailPrint-Setup-win-x64.exe'

Write-Host 'Đang tạo Retail Print cho Windows...'

dotnet restore $project
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore thất bại với mã $LASTEXITCODE"
}

Remove-Item $output -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o $output
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish thất bại với mã $LASTEXITCODE"
}

$exe = Join-Path $output 'RetailPrint.exe'
if (-not (Test-Path $exe)) {
    throw 'Không tạo được RetailPrint.exe.'
}

$looseDlls = Get-ChildItem $output -File -Filter '*.dll'
if ($looseDlls) {
    $looseDlls | Select-Object Name, Length | Format-Table -AutoSize
    throw 'Gói single-file còn DLL rời; bộ cài sẽ thiếu thư viện native nếu chỉ đóng gói RetailPrint.exe.'
}

Write-Host 'Đang kiểm tra khả năng khởi động...'
$process = Start-Process -FilePath $exe -ArgumentList '--smoke-test' -PassThru -Wait
if ($process.ExitCode -ne 0) {
    $log = Join-Path $env:LOCALAPPDATA 'RetailPrint\Logs\startup.log'
    if (Test-Path $log) {
        Get-Content $log -Tail 120
    }
    throw "RetailPrint.exe không vượt qua kiểm tra khởi động. Mã $($process.ExitCode)"
}

$isccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw 'Chưa có Inno Setup 6. Cài Inno Setup 6 để tạo RetailPrint-Setup.exe.'
}

Write-Host 'Đang tạo bộ cài...'
& $iscc '/DMyAppVersion=2.0.0' $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Tạo bộ cài thất bại với mã $LASTEXITCODE"
}
if (-not (Test-Path $installerOutput)) {
    throw 'Không tạo được RetailPrint-Setup-win-x64.exe.'
}

Write-Host "Đã tạo ứng dụng: $exe"
Write-Host "Đã tạo bộ cài: $installerOutput"

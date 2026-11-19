param([switch]$BuildOnly)

$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Join-Path $PSScriptRoot 'src'
$tests = Join-Path $PSScriptRoot 'tests'
$assets = Join-Path $PSScriptRoot 'assets'
$engine = Join-Path $PSScriptRoot 'vendor\7zr.exe'
$bin = Join-Path $PSScriptRoot 'build'
$mock = Join-Path $bin 'test\mock'
$runs = Join-Path $bin 'test\runs'
New-Item -ItemType Directory -Force -Path "$bin\app" | Out-Null

$base = @('/nologo', '/codepage:65001', '/optimize+', '/reference:System.IO.Compression.dll',
          '/reference:System.Security.dll', "/resource:$engine,OracleBackup.7zr.exe",
          "$source\Transfer.cs", "$source\Packing.cs", "$source\Preferences.cs", "$source\Pairing.cs")
$client = @('/reference:System.Xml.dll', "$source\OracleWork.cs", "$source\BackupTasks.cs")
$forms = @('/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll')
$backupIcon = "/resource:$assets\Backup.ico,OracleBackup.Backup.ico"
$receiveIcon = "/resource:$assets\Receive.ico,OracleBackup.Receive.ico"
$clientName = [string]::Concat('Oracle', [char]0x5907, [char]0x4EFD, [char]0x7AEF, '.exe')
$serverName = [string]::Concat('Oracle', [char]0x63A5, [char]0x53D7, [char]0x7AEF, '.exe')

function Compile {
    param([string[]]$Options)
    $flags = @($Options | Where-Object { $_.StartsWith('/') })
    $files = @($Options | Where-Object { -not $_.StartsWith('/') })
    & $csc @flags @files
    if ($LASTEXITCODE -ne 0) { throw 'Compiler failed.' }
}

Compile ($base + $client + $forms + @(
    '/target:winexe', '/define:CLIENT_BUILD', "/out:$bin\app\$clientName",
    "/win32icon:$assets\Backup.ico", $backupIcon, "$source\ClientForm.cs", "$source\ProgramInfo.cs"
))
Compile ($base + $forms + @(
    '/target:winexe', '/define:SERVER_BUILD', "/out:$bin\app\$serverName",
    "/win32icon:$assets\Receive.ico", $receiveIcon, "$source\ServerForm.cs", "$source\ProgramInfo.cs"
))
if ($BuildOnly) { return }

New-Item -ItemType Directory -Force -Path $mock, $runs | Out-Null
Compile @('/nologo', '/codepage:65001', '/target:exe', "/out:$mock\expdp.exe", "$tests\MockOracle.cs")
foreach ($name in @('impdp.exe', 'sqlplus.exe')) {
    Copy-Item -LiteralPath "$mock\expdp.exe" -Destination "$mock\$name" -Force
}

$both = $base + $client + @('/target:exe', '/define:SERVER_BUILD,CLIENT_BUILD,TEST_BUILD')
Compile ($both + @("/out:$bin\test\Harness.exe", "$tests\Harness.cs"))
Compile ($both + $forms + @(
    '/main:ClientUiSmoke', "/out:$bin\test\ClientUiSmoke.exe", $backupIcon, "$source\ClientForm.cs", "$tests\ClientUiSmoke.cs"
))
Compile ($both + $forms + @(
    '/main:UiRender', "/out:$bin\test\UiRender.exe", $backupIcon, $receiveIcon,
    "$source\ClientForm.cs", "$source\ServerForm.cs", "$tests\UiRender.cs"
))

& "$bin\test\Harness.exe" "$runs" "$mock"
if ($LASTEXITCODE -ne 0) { throw 'Oracle and TLS simulation failed.' }
$uiRun = Join-Path $runs ("ui_" + [Guid]::NewGuid().ToString('N'))
& "$bin\test\ClientUiSmoke.exe" "$uiRun" "$bin\test\client-smoke.png" "$mock"
if ($LASTEXITCODE -ne 0) { throw 'UI smoke test failed.' }
& "$bin\test\UiRender.exe" "$bin\test"
if ($LASTEXITCODE -ne 0) { throw 'UI preview failed.' }

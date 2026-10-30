# Local synthetic telemetry only. Run in a dedicated foreground terminal.
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'This launcher requires Windows.' }
$node = (Get-Command node.exe -ErrorAction Stop).Source
$npx = (Get-Command npx.cmd -ErrorAction Stop).Source
foreach ($port in @(18888, 4317, 4318)) {
    if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) {
        throw "Port $port is already in use; no process was stopped."
    }
}
$root = Join-Path $env:LOCALAPPDATA 'Temp\opencode\MVP39-local-observability-runtime'
New-Item -ItemType Directory -Force -Path $root | Out-Null
# A fresh working directory also prevents a project .npmrc from changing npm config.
$session = Join-Path $root ('session-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $session | Out-Null
$userConfig = Join-Path $session 'user.npmrc'
$globalConfig = Join-Path $session 'global.npmrc'
foreach ($config in @($userConfig, $globalConfig)) {
    [System.IO.File]::Open($config, [System.IO.FileMode]::CreateNew).Dispose()
}
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $env:ComSpec
$psi.WorkingDirectory = $session
$psi.UseShellExecute = $false
# A whitelist prevents inherited ASP.NET, dashboard, proxy and authentication overrides.
$psi.EnvironmentVariables.Clear()
foreach ($name in @('SystemRoot','WINDIR','ComSpec','PATH','PATHEXT','USERPROFILE','LOCALAPPDATA','APPDATA','PROGRAMDATA','NUMBER_OF_PROCESSORS','PROCESSOR_ARCHITECTURE')) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if ($value) { $psi.EnvironmentVariables[$name] = $value }
}
$psi.EnvironmentVariables['TEMP'] = $root
$psi.EnvironmentVariables['TMP'] = $root
$psi.EnvironmentVariables['ASPIRE_HOME'] = Join-Path $root 'aspire-home'
$psi.EnvironmentVariables['ASPIRE_NPM_CACHE_DIR'] = Join-Path $root 'aspire-npm'
$psi.EnvironmentVariables['npm_config_cache'] = Join-Path $root 'npm-cache'
$psi.EnvironmentVariables['npm_config_userconfig'] = $userConfig
$psi.EnvironmentVariables['npm_config_globalconfig'] = $globalConfig
$psi.EnvironmentVariables['npm_config_registry'] = 'https://registry.npmjs.org/'
$psi.EnvironmentVariables['npm_config_ignore_scripts'] = 'true'
$psi.EnvironmentVariables['npm_config_update_notifier'] = 'false'
$psi.EnvironmentVariables['ASPIRE_CLI_TELEMETRY_OPTOUT'] = 'true'
$psi.EnvironmentVariables['DASHBOARD__FRONTEND__AUTHMODE'] = 'BrowserToken'
$psi.EnvironmentVariables['DASHBOARD__API__AUTHMODE'] = 'ApiKey'
$psi.EnvironmentVariables['DASHBOARD__OTLP__AUTHMODE'] = 'Unsecured'
$psi.Arguments = '/d /s /c ""' + $npx + '" --yes --package=@microsoft/aspire-cli@13.6.4 aspire dashboard run --frontend-url http://127.0.0.1:18888 --otlp-http-url http://127.0.0.1:4318 --otlp-grpc-url http://127.0.0.1:4317 --persistence None --Dashboard:TelemetryLimits:MaxLogCount=1000 --Dashboard:TelemetryLimits:MaxTraceCount=100 --Dashboard:TelemetryLimits:MaxMetricsCount=1000 --Dashboard:TelemetryLimits:MaxResourceCount=10 --Dashboard:TelemetryLimits:MaxAttributeCount=32 --Dashboard:TelemetryLimits:MaxAttributeLength=256"'
$process = [System.Diagnostics.Process]::Start($psi)
try {
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Aspire exited with code $($process.ExitCode)." }
} finally { $process.Dispose() }

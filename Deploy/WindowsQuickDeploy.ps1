$exeName = "Barotrauma"  # Replace with the name of your executable

# Check if the process is running
$process = Get-Process -Name $exeName -ErrorAction SilentlyContinue

if ($process) {
    # If the process is running, kill it
    Write-Host "$exeName is running. Terminating it..."
    Stop-Process -Name $exeName -Force
    Write-Host "$exeName has been terminated."
} else {
    Write-Host "$exeName is not running."
}

$sourceDir = "C:\Program Files (x86)\Steam\steamapps\common\Barotrauma\Content"
$destDir = ".\bin\content\Windows\Client\Content"

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "cmd.exe"
$psi.Arguments = "/c DeployAll.bat"
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true

$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi
$proc.Start() | Out-Null

$writer = $proc.StandardInput
$reader = $proc.StandardOutput

while (-not $reader.EndOfStream) {
    $line = $reader.ReadLine()
    Write-Host $line

    if ($line -match "Do you still wish to proceed\?.*\[y/n\]") {
        Start-Sleep -Milliseconds 200
        $writer.WriteLine("y")
    }
    elseif ($line -match "Type 1 for Release, 2 for Unstable") {
        Start-Sleep -Milliseconds 200
        $writer.WriteLine("1")
    }
    elseif ($line -match "Deploy/bin/content\\Mac") {
        # Kill the main process and all its child processes
        $parentId = $proc.Id
        Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $parentId } | ForEach-Object {
            try {
                Stop-Process -Id $_.ProcessId -Force
                Write-Host "Killed $($_.ProcessId)"
            } catch {
                Write-Host "Could not kill child process ID $($_.ProcessId)"
            }
        }

        # Now kill the main one
        try {
            Stop-Process -Id $proc.Id -Force
            Write-Host "Killed $($proc.Id)"
        } catch {
            Write-Host "Could not kill main process ID $($proc.Id)"
        }

        Get-ChildItem -Path $sourceDir -Recurse | ForEach-Object {
            $relativePath = $_.FullName.Substring($sourceDir.Length).TrimStart('\')
            $targetPath = Join-Path $destDir $relativePath

            if (-not (Test-Path $targetPath)) {
                New-Item -ItemType Directory -Path (Split-Path $targetPath) -Force | Out-Null
                Copy-Item -Path $_.FullName -Destination $targetPath
            }
        }
        Write-Host "Copy Data Done"
        Start-Process ".\bin\content\Windows\Client\Barotrauma.exe"
        break
    }
}

$proc.WaitForExit()

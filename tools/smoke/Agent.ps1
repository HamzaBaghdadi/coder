param([string]$Exe, [string]$Data, [string]$Shots)

# Real-window check of Coder Agent against a fake Ollama server on this machine (no internet, no keys):
# the chat opens from the command palette, a message sent with Enter reaches the server, the agent's
# edit shows as a diff card, Accept writes the file, and the reply streams in after it. The instance
# has its own --user-data-dir, so your own settings and chats are untouched.
#   .\Agent.ps1 -Exe <Coder.exe> -Data $env:TEMP\coder-agent-smoke -Shots $env:TEMP\coder-agent-shots
. "$PSScriptRoot\SmokeCommon.ps1"
$ErrorActionPreference = 'Stop'

function Wait-For([scriptblock]$condition, [int]$seconds = 30) {
  $deadline = (Get-Date).AddSeconds($seconds)
  while (-not (& $condition) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
  return [bool](& $condition)
}

function Run-Command([string]$title) {
  Invoke 'Search files and commands'
  Set-Text 'Command palette input' ">$title"
  Post-Enter
}

Remove-Item -Recurse -Force $Data -ErrorAction SilentlyContinue
$folder = Join-Path $Data 'project'
New-Item -ItemType Directory -Force $folder | Out-Null
$file = Join-Path $folder 'hello.txt'
[IO.File]::WriteAllText($file, "Hello`nWorld`n")

# A free port, then the fake Ollama in a background runspace.
$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $probe.Start(); $port = $probe.LocalEndpoint.Port; $probe.Stop()
$state = [hashtable]::Synchronized(@{ Chats = 0; Bodies = [Collections.ArrayList]::new() })
$server = [PowerShell]::Create()
[void]$server.AddScript({
  param($port, $state)
  $listener = [Net.HttpListener]::new(); $listener.Prefixes.Add("http://localhost:$port/"); $listener.Start(); $state.Listener = $listener
  while ($listener.IsListening) {
    $context = $listener.GetContext()
    $body = [IO.StreamReader]::new($context.Request.InputStream).ReadToEnd()
    $reply = switch ($context.Request.Url.AbsolutePath) {
      '/api/tags' { '{"models":[{"name":"smoke-model"}]}' }
      '/api/show' { '{"capabilities":["completion","tools"]}' }
      '/api/chat' {
        $state.Chats++; [void]$state.Bodies.Add($body)
        if ($state.Chats -eq 1) {
          '{"message":{"role":"assistant","content":"I will greet the agent."},"done":false}' + "`n" +
          '{"message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"edit_file","arguments":{"path":"hello.txt","old_text":"Hello","new_text":"Hello, agent"}}}]},"done":false}' + "`n" +
          '{"message":{"role":"assistant","content":""},"done":true,"done_reason":"stop"}'
        } else {
          '{"message":{"role":"assistant","content":"Done: **hello.txt** now greets the agent."},"done":false}' + "`n" +
          '{"message":{"role":"assistant","content":""},"done":true,"done_reason":"stop"}'
        }
      }
      default { $context.Response.StatusCode = 404; '{"error":"not found"}' }
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes($reply)
    $context.Response.ContentType = 'application/json'
    $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $context.Response.Close()
  }
}).AddArgument($port).AddArgument($state)
$async = $server.BeginInvoke()

# Settings: only the fake Ollama, chosen. No key is involved.
New-Item -ItemType Directory -Force $Data | Out-Null
@{
  agentProviders = @(@{ id = 'smoke'; kind = 'ollama'; name = 'Smoke Ollama'; baseUrl = "http://localhost:$port" })
  agentProvider = 'smoke'
  agentModel = 'smoke-model'
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Data 'settings.json') -Encoding UTF8

Start-Coder $Exe $Data $folder
try {
  Start-Sleep -Seconds 2
  Run-Command 'Coder Agent: Open Chat'
  Check 'chat opened' (Wait-For { $null -ne (Find 'Message Coder Agent') } 10)
  Set-Text 'Message Coder Agent' 'Greet the agent in hello.txt'
  Post-Enter
  Check 'message reached the provider' (Wait-For { $state.Chats -ge 1 } 20)
  Check 'request offered the tools' ($state.Bodies[0] -match '"edit_file"')
  Check 'diff card shown' (Wait-For { $null -ne (Find 'Accept change to hello.txt') } 20)
  Check 'file unchanged before Accept' ((Get-Content $file -Raw) -eq "Hello`nWorld`n")
  Shot 'agent-diff'
  Invoke 'Accept change to hello.txt'
  Check 'Accept wrote the file' (Wait-For { (Get-Content $file -Raw) -eq "Hello, agent`nWorld`n" } 10) (Get-Content $file -Raw)
  Check 'agent continued after Accept' (Wait-For { $state.Chats -ge 2 } 20)
  Check 'model heard it was accepted' ($state.Bodies[1] -match 'accepted')
  Start-Sleep -Seconds 1
  Shot 'agent-done'
  Check 'still running' (Alive)
}
finally {
  Stop-Coder
  if ($state.Listener) { $state.Listener.Stop() }; $server.Dispose()
}

$settings = Get-Content (Join-Path $Data 'settings.json') -Raw
Check 'provider kept in settings' ($settings -match 'Smoke Ollama')

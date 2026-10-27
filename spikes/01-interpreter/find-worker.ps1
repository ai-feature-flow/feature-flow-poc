# Print the process id of the running spike worker, one per line.
# The worker is started with `dotnet run` from its own directory, so its command
# line is "dotnet run --no-build -- --queue <queue> --sandbox <url>". Match on
# the --queue argument, which only the worker passes.
param([string]$Queue = 'spike-1')

Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" |
    Where-Object { $_.CommandLine -like "*--queue $Queue*" } |
    Select-Object -ExpandProperty ProcessId

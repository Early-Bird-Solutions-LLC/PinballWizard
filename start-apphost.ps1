# Convenience launcher for the Aspire AppHost. Launches via the Aspire CLI
# ("aspire run") so the running AppHost is registered with the CLI — enabling
# "aspire agent mcp" (and the committed .mcp.json) to attach and give AI coding
# agents live log/trace/resource access to the local environment.
#
# The --apphost flag points the CLI at the AppHost project from the repo root,
# so Push-Location is no longer required.

aspire run --apphost src\PinballWizard.AppHost

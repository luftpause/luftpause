# Security

Report vulnerabilities through GitHub's private vulnerability reporting on this
repository (Security tab → Report a vulnerability). Do not open a public issue
for them. The latest release is the supported version.

What to know when deploying:

- Both doors, the OpenAI-compatible proxy on `:8080` and the MCP server on
  `:7000`, accept any caller unless `LUFTPAUSE_TOKEN` is set, and the scheduler's
  status page on `:5001` is always open. Set the token and keep the ports off the
  public internet.
- The proxy forwards the engine credentials configured for a replica, never the
  caller's `Authorization` header.
- Sub-agents run with every tool server configured for the runtime unless `spawn`
  restricts them with `tools`. Treat the configured MCP servers as reachable by
  anyone who can reach the runtime.

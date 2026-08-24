<!-- idd:project:start -->
## Intent-Driven Development

This project uses Intent-Driven Development (IDD). Treat `.idd/intent/` as the current product truth and use the installed IDD skills when changing intent, implementing behavior, or verifying the implementation.

For any user-interface work, read and follow `.idd/intent/IDD-0023.spec-compact-user-interface.md`.
<!-- idd:project:end -->

## Development startup

For coding-agent development and browser automation, run the application as a backend-only ASP.NET Core host:

```bash
dotnet run --project ChatClient.Api --launch-profile backend
```

The backend profile uses `http://127.0.0.1:5080`, does not launch a browser, and exposes `GET /healthz` for readiness checks. Use `dotnet watch` with the same project/profile when automatic restart after source changes is useful.

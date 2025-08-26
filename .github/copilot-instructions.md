## Project Coding Standards

### Comments
- Avoid obvious comment.
- Only add comments when they add real value.
- Write all comments in English.
- If consider adding comment to variable check mey be it's better to adjust variable name

### Control Flow
- Minimize use of `if…else`; prefer guard clauses and early `return`.
- Avoid deep nesting of `if` statements; use early exits to keep methods flat.
- Create router methods that delegate to appropriate specialized services based on conditions.

### JSON Serialization
- Use `System.Text.Json` for all JSON (de)serialization.

### MudBlazor
- MudDialog visibility property is "Visible", not IsVisible

### C# Conventions
- Use PascalCase for classes, methods, properties, and public fields.
- Use camelCase for parameters, local variables, and private fields.
- Prefix interfaces with `I` (e.g., `IUserService`).
- Use primary constructors when appropriate (C# 12+).

### Error Handling
- Return empty collections (`[]`) instead of null when no data is available.
- Log errors with structured logging using ILogger.

### Architecture
- Follow Clean Architecture principles with clear separation of concerns.
- Use dependency injection for service registration and resolution.
- Organize code into logical layers: API, Services, Models, Shared.
- Follow DRY principle: extract duplicated logic into helper classes or services.
- Create specialized services for complex operations instead of embedding logic in multiple places.

### Modern C# Features
- Use target-typed expressions where appropriate (`return [];`).
- Leverage nullable reference types for better null safety.
- Use pattern matching and modern C# syntax when it improves readability.

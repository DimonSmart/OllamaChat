## Project Coding Standards

### Comments
- Avoid stating the obvious.
- Only add comments when they add real value.
- Write all comments in English.

### Control Flow
- Minimize use of `if…else`; prefer guard clauses and early `return`.
- Avoid deep nesting of `if` statements; use early exits to keep methods flat.

### JSON Serialization
- Use `System.Text.Json` for all JSON (de)serialization.

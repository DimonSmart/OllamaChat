# Knowledge Book MCP

## Что это

Built-in MCP server `built-in-knowledge-book` хранит иерархическую книгу знаний в одном JSON-файле и дает короткие outline-ссылки вида `1`, `1.2`, `1.2.3`.

## Доступные tools

- `kb_get_context`
- `kb_list_headings`
- `kb_get_section`
- `kb_insert_section`
- `kb_update_section`
- `kb_search_sections`
- `kb_export_markdown`

## Ссылки на разделы

- `0` — корень книги
- `1` — первый заголовок верхнего уровня
- `1.2` — второй дочерний заголовок раздела `1`
- `1.2.3` — третий дочерний заголовок раздела `1.2`

Поле `Path` в ответах остается человекочитаемым breadcrumb. Для навигации между MCP вызовами нужно использовать именно `Outline`.

## Чтение

- `kb_list_headings(outline: "0")` — верхний уровень
- `kb_list_headings(outline: "1.2")` — дети `1.2`
- `kb_get_section(outline: "1.2.3")` — конкретный раздел

## Создание

Создание нового раздела теперь идет через `kb_insert_section`.

Параметры:

- `title` — заголовок нового раздела
- `anchorOutline` — опорная ссылка для вставки
- `asChild` — если `true`, вставить внутрь `anchorOutline` в конец; если `false`, вставить после `anchorOutline` на том же уровне
- `contentMarkdown` — начальный markdown контент

Правила вставки:

- пустой `anchorOutline` — добавить в конец верхнего уровня
- реальный `anchorOutline`, например `2.3`, и `asChild=false` — вставить после `2.3` на том же уровне
- реальный `anchorOutline`, например `2.3`, и `asChild=true` — вставить в конец детей `2.3`
- `anchorOutline="0"` и `asChild=false` — вставить в начало верхнего уровня
- `anchorOutline="1.2.0"` и `asChild=false` — вставить в начало детей раздела `1.2`

Примеры:

```json
{
  "title": "Readme"
}
```

```json
{
  "title": "Intro",
  "anchorOutline": "0",
  "asChild": false
}
```

```json
{
  "title": "Eclairs",
  "anchorOutline": "1.2",
  "asChild": true,
  "contentMarkdown": "Use choux pastry and pastry cream."
}
```

## Обновление

Обновление существующего раздела теперь отделено от создания и идет через `kb_update_section`.

Параметры:

- `outline` — ссылка на существующий раздел
- `contentMarkdown` — новый markdown контент

Поведение:

- старый контент всегда заменяется новым
- пустая строка очищает контент раздела

Пример:

```json
{
  "outline": "1.2.3",
  "contentMarkdown": "Final version of the section."
}
```

## Типичный flow

1. Вызвать `kb_list_headings(outline: "0")`.
2. Выбрать нужный раздел по `Outline`.
3. Для чтения вызвать `kb_get_section(outline: "...")`.
4. Для добавления соседнего раздела вызвать `kb_insert_section(title: "...", anchorOutline: "...")`.
5. Для добавления дочернего раздела вызвать `kb_insert_section(title: "...", anchorOutline: "...", asChild: true)`.
6. Для правки содержимого вызвать `kb_update_section(outline: "...", contentMarkdown: "...")`.

## Пример привязки knowledge file к агенту

```json
{
  "AgentName": "Book Knowledge Agent",
  "ShortName": "BKA",
  "FunctionSettings": {
    "AutoSelectCount": 0,
    "SelectedFunctions": [
      "Built-in Knowledge Book MCP Server:kb_list_headings",
      "Built-in Knowledge Book MCP Server:kb_get_section",
      "Built-in Knowledge Book MCP Server:kb_insert_section",
      "Built-in Knowledge Book MCP Server:kb_update_section",
      "Built-in Knowledge Book MCP Server:kb_search_sections"
    ]
  },
  "McpServerBindings": [
    {
      "ServerId": "da46c3f1-6bc6-4f0b-bd7b-6176daf6f6d8",
      "Parameters": {
        "knowledgeFile": "C:\\Work\\Cookbook\\knowledge\\cookbook.json"
      }
    }
  ]
}
```

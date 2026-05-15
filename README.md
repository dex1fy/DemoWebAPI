# DemoWebAPI

DemoWebAPI - это демонстрационный ASP.NET Web API для PostgreSQL базы данных проекта `aml_task`.

API решает две задачи:

- показывает фактическую структуру базы данных: таблицы, колонки, количество строк и содержимое таблиц;
- демонстрирует бизнес-правила базы данных: транзакции, аудит, ограничения внешних ключей, изменение статусов и порядка задач.

Проект использует:

- ASP.NET Core Minimal API;
- Entity Framework Core;
- PostgreSQL provider `Npgsql.EntityFrameworkCore.PostgreSQL`;
- Swagger UI для ручного тестирования API.

## Как работает подключение к базе

База данных находится на удаленном сервере и недоступна напрямую извне. Поэтому локально API подключается к ней через SSH-туннель.

Схема подключения:

```text
локальный API -> localhost:15432 -> SSH tunnel -> сервер -> PostgreSQL:5432
```

Для самого API база выглядит как локальная:

```text
Host=localhost;Port=15432;Database=aml_db;Username=aml_user;Password=<password>
```

## Локальный запуск

### 1. Открыть SSH-туннель

В отдельном терминале выполни:

```powershell
ssh -L 15432:127.0.0.1:5432 arzamasova_dar@194.156.118.99
```

Терминал с SSH нужно оставить открытым, пока работает API.

### 2. Настроить строку подключения

Перейди в папку проекта:

```powershell
cd C:\Users\arzam\RiderProjects\DemoWebAPI\DemoWebAPI
```

Задай connection string через user-secrets:

```powershell
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=15432;Database=aml_db;Username=aml_user;Password=<password>"
```

Пароль не нужно хранить в `appsettings.json`.

### 3. Запустить API

```powershell
dotnet run
```

По текущим настройкам проект запускается на:

```text
http://localhost:5026
https://localhost:7058
```

Swagger доступен по адресу:

```text
http://localhost:5026/swagger
```

## Проверка подключения

Сначала проверь:

```http
GET /api/health
```

Если все настроено правильно, API вернет статус подключения, имя базы, пользователя PostgreSQL, версию сервера и время базы данных.

## Ручки для просмотра базы

Эти endpoints нужны, чтобы увидеть реальную структуру базы на сервере.

### Получить список таблиц

```http
GET /api/tables
```

Показывает все таблицы в схеме `aml_task`.

### Получить колонки таблицы

```http
GET /api/tables/{tableName}/columns
```

Пример:

```http
GET /api/tables/issues/columns
```

### Получить количество строк

```http
GET /api/tables/{tableName}/count
```

Пример:

```http
GET /api/tables/users/count
```

### Получить строки таблицы

```http
GET /api/tables/{tableName}/rows?limit=50&offset=0
```

Примеры:

```http
GET /api/tables/projects/rows
GET /api/tables/statuses/rows
GET /api/tables/issues/rows?limit=100
GET /api/tables/audit_log/rows?limit=20
```

`limit` ограничен на стороне API, чтобы случайно не выгрузить слишком много данных.

## Ручки для демонстрации бизнес-правил

Эти endpoints уже изменяют данные в базе. Перед использованием лучше посмотреть реальные `id` через `/api/tables/.../rows`.

### Завершить спринт

```http
POST /api/projects/{projectId}/sprints/{sprintId}/complete
```

Что демонстрирует:

- выполнение нескольких изменений в одной транзакции;
- перевод спринта в статус `completed`;
- заполнение `completed_at`;
- перенос незавершенных задач из спринта обратно в backlog/todo;
- сохранение завершенных задач в их текущем состоянии.

Перед вызовом можно посмотреть спринты:

```http
GET /api/tables/sprints/rows
```

И задачи:

```http
GET /api/tables/issues/rows
```

### История задачи

```http
GET /api/projects/{projectId}/issues/{issueId}/history
```

Что демонстрирует:

- таблицу `issue_status_history`;
- технический аудит из `audit_log`;
- события смены статуса;
- пользователя, который выполнил изменение, если он был передан в контекст БД.

Перед вызовом можно посмотреть задачи:

```http
GET /api/tables/issues/rows
```

### Удаление статуса с проверкой ограничений

```http
DELETE /api/projects/{projectId}/statuses/{statusId}
```

Что демонстрирует:

- защиту данных от удаления используемого статуса;
- проверку связанных задач;
- возврат `409 Conflict`, если в статусе есть задачи.

Пример ответа при конфликте:

```json
{
  "statusCode": 409,
  "message": "Cannot delete status with existing issues. Issues count: 3."
}
```

Перед вызовом можно посмотреть статусы:

```http
GET /api/tables/statuses/rows
```

### Drag-and-drop задачи

```http
PATCH /api/projects/{projectId}/issues/{issueId}/position
```

Пример тела запроса:

```json
{
  "statusId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbb002",
  "rankPosition": 1500,
  "changedBy": "11111111-1111-1111-1111-111111111111"
}
```

Что демонстрирует:

- изменение `status_id`;
- изменение `rank_position`;
- обновление `updated_at`;
- запись события в `issue_status_history`;
- работу audit-триггера через `app.current_user_id`.

После вызова можно проверить:

```http
GET /api/tables/issues/rows
GET /api/tables/issue_status_history/rows
GET /api/tables/audit_log/rows?limit=20
```

## Рекомендуемый порядок демонстрации

1. `GET /api/health` - показать, что API подключено к PostgreSQL.
2. `GET /api/tables` - показать наличие таблиц в схеме `aml_task`.
3. `GET /api/tables/issues/rows` - показать реальные задачи.
4. `GET /api/tables/statuses/rows` - выбрать статус для проверки.
5. `DELETE /api/projects/{projectId}/statuses/{statusId}` - показать `409 Conflict` при попытке удалить используемый статус.
6. `PATCH /api/projects/{projectId}/issues/{issueId}/position` - изменить статус/позицию задачи.
7. `GET /api/projects/{projectId}/issues/{issueId}/history` - показать историю изменений.
8. `GET /api/tables/audit_log/rows?limit=20` - показать технический аудит.
9. `POST /api/projects/{projectId}/sprints/{sprintId}/complete` - показать транзакционный сценарий завершения спринта.

## Важные замечания

- API работает со схемой `aml_task`.
- Explorer endpoints только читают данные.
- Business endpoints изменяют данные в базе.
- Для локального запуска нужен открытый SSH-туннель.
- Если API запущен, повторная сборка может ругаться на заблокированные DLL в `bin`. В этом случае останови API или проверяй сборку командой:

```powershell
dotnet build -o .build-check
```

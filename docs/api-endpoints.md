# API Endpoints Documentation (актуально по коду)

Источник: контроллеры в `AdsPortalV2.1/Controllers` и DTO в `AdsPortalV2.1/Models`.

---

## AdminController (`/admin`, `[Authorize]`)

### Ads
- `GET /admin/ads` (`CanViewHiddenAd`)
  - Query: `skip=0`, `take=50` (clamp `1..200`), `status` (CSV enum `AdStatus`)
  - Response: `AdminAdsResultDto`
    - `total: int`
    - `items: AdminAdListItemDto[]`
- `POST /admin/ads/{id:int}/approve` (`CanModerateAd`) -> `AdStatusActionDto`
- `POST /admin/ads/{id:int}/reject` (`CanModerateAd`)
  - Body: `{ "reason": string? }`
  - Response: `AdStatusActionDto`
- `POST /admin/ads/{id:int}/send-to-moderation` (`CanModerateAd`) -> `AdStatusActionDto`
- `DELETE /admin/ads/{id:int}` (`CanModerateAd`) -> `AdStatusActionDto` (soft delete)
- `POST /admin/ads/{id:int}/restore` (`CanModerateAd`) -> `AdStatusActionDto`
- `DELETE /admin/ads/{id:int}/hard` (`CanModerateAd`) -> `AdStatusActionDto` (hard delete)

### Users / Logs
- `GET /admin/users` (`CanViewLogs`)
  - Query: `skip=0`, `take=50` (clamp `1..200`)
  - Response: `AdminUsersResultDto`
- `GET /admin/logs` (`CanViewLogs`)
  - Query: `skip=0`, `take=50` (clamp `1..200`)
  - Response: `AdminLogsResultDto`

### Restrictions
- `GET /admin/restrictions/types` (`CanBanUser`)
  - Response: `RestrictionTypeDto[]`
- `POST /admin/users/{id:int}/restrictions` (`CanBanUser`)
  - Body: `{ "type": "LoginBan|PostBan|ChatBan", "reason"?: string, "expiresAt"?: ISO datetime }`
  - Response: `RestrictionActionDto`
- `DELETE /admin/users/{id:int}/restrictions/{type}` (`CanUnbanUser`)
  - `type`: `LoginBan|PostBan|ChatBan`
  - Response: `RestrictionActionDto`

### Roles
- `POST /admin/users/{id:int}/roles` (`CanAssignRole`)
  - Body: `RoleDto` (`{ role: string }`)
  - Response: `RoleActionDto`
- `DELETE /admin/users/{id:int}/roles/{role}` (`CanRevokeRole`)
  - Response: `RoleActionDto`

---


## AdsController (`/ads`) — подробная контрактная спецификация

Общие правила для всех эндпоинтов в разделе:
- Все ответы JSON сериализуются в camelCase.
- Даты возвращаются в ISO-8601 с временной зоной (UTC), например: `2025-12-01T12:00:00Z`.
- Enum'ы возвращаются и принимаются как camelCase строки.

AdStatus (enum)
- `active`
- `pendingModeration`
- `rejected`
- `deleted`

POST /ads
Request
- Content-Type: `multipart/form-data` (браузер добавляет boundary)

Форма (field | type | required)
- `title` | string | ✅
- `description` | string | ❌ (nullable)
- `price` | number | ❌ (nullable)
- `isNegotiable` | boolean (`true`/`false`) | ❌
- `categoryId` | int | ✅
- `locationId` | int | ✅
- `listingType` | string | ❌
- `mainImageIndex` | int (0-based) | ❌
- `files` | file (repeatable) | ❌ (каждый файл добавлять `formData.append("files", file)`)

Response 200 (CreateAdResultDto)
```json
{
  "message": "Ad created with images successfully.",
  "adId": 123,
  "images": [
    {
      "id": 1,
      "adId": 123,
      "filePath": "files/5/Ads/123/img1.jpg",
      "sortOrder": 0
    }
  ]
}
```

GET /ads
Request (query)
- `search?: string`
- `location?: string` (CSV ints, max 10)
- `category?: string` (CSV ints)
- `priceFrom?: number`
- `priceTo?: number`
- `dateFrom?: YYYY-MM-DD` (DateOnly)
- `dateTo?: YYYY-MM-DD` (DateOnly, inclusive)
- `userId?: int`
- `status?: active|pendingModeration|rejected|deleted`
- `type?: string`
- `page?: int` (default 1)
- `pageSize?: int` (default 20, clamp 1..50)
- `sort?: string` (whitelist: `title, price, createdAt, updatedAt, views, favorites`, use `-` prefix for desc)

Response 200 (PagedResultDto<AdListItemDto>)
```json
{
  "items": [
    {
      "id": 123,
      "title": "Продам велосипед",
      "description": "Хорошее состояние",
      "price": 100.5,
      "isNegotiable": false,
      "categoryId": 5,
      "locationId": 2,
      "location": { "type": "city", "id": 2, "name": "Москва" },
      "listingType": null,
      "createdAt": "2025-12-01T12:00:00Z",
      "updatedAt": "2025-12-02T12:00:00Z",
      "userId": 5,
      "viewsCount": 10,
      "favoritesCount": 2,
      "mainImageUrl": "/files/5/Ads/123/img1.jpg",
      "isFavorite": false,
      "moderationStatus": "pendingModeration"
    }
  ],
  "total": 1,
  "page": 1,
  "pageSize": 20,
  "totalPages": 1
}
```

GET /ads/{id}
Response 200 (AdDetailsDto)
```json
{
  "id": 123,
  "userId": 5,
  "categoryId": 10,
  "title": "Продам диван",
  "description": "Классический диван",
  "price": 250.0,
  "isNegotiable": true,
  "locationId": 3,
  "location": { "type": "region", "id": 1, "name": "Московская область" },
  "listingType": null,
  "createdAt": "2025-12-01T12:00:00Z",
  "updatedAt": "2025-12-02T12:00:00Z",
  "moderationStatus": "active",
  "rejectionReason": null,
  "deletedAt": null,
  "category": { "id": 10, "name": "Мебель", "parentId": null },
  "user": {
    "id": 5,
    "userLogin": "ivan",
    "userName": "Иван Иванов",
    "userEmail": "ivan@example.com",
    "userPhoneNumber": "+70000000000",
    "avatarPath": null,
    "roles": ["user"],
    "createdAt": "2024-01-01T00:00:00Z",
    "lastActivityAt": "2025-12-02T12:00:00Z"
  },
  "images": [
    { "id": 1, "adId": 123, "filePath": "files/5/Ads/123/img1.jpg", "sortOrder": 0 }
  ],
  "isFavorite": false
}
```

PATCH /ads/{id}
Request
- Content-Type: `application/json`
- Body: `UpdateAdDto` (all fields optional). Поля и форматы соответствуют `AdsPortalV2.1/Models/UpdateAdDto.cs`.

Пример body
```json
{
  "title": "Новый заголовок",
  "price": 200.5,
  "mainImageId": 3,
  "images": [
    { "filePath": "files/5/Ads/123/new.jpg", "sortOrder": 2 }
  ]
}
```

Response 200 (PatchResultDto)
```json
{
  "success": true,
  "updated": ["Title", "MainImageId"],
  "skipped": [],
  "errors": []
}
```

POST /ads/{id}/upload
Request
- Content-Type: `multipart/form-data`
- Files field name: `files` (repeatable)

Response 200 (UploadFilesResultDto)
```json
{
  "files": ["files/5/Ads/123/new1.jpg", "files/5/Ads/123/new2.jpg"]
}
```

PATCH /ads/{id}/moderation
Request
- Auth policy: `CanModerateAd`
- Content-Type: `application/json`
- Body: `AdStatus` (string enum), например: `"active"`

Response 200
- Returns `AdDto` (текущее состояние объявления).

GET /ads/moderation
Request
- Auth policy: `CanModerateAd`

Response 200
- `ModerationAdDto[]` — содержит `mainImageUrl` (string | null) и другие краткие поля объявления.

Notes for frontend
- Всегда отправлять `files` как repeatable field с именем `files` (не `files[]`).
- Для формирования ссылок на изображения используйте `"/" + filePath` или `window.location.origin + "/" + filePath`.
- Для изменения главного изображения используйте `PATCH /ads/{id}` с `mainImageId` равным `AdImageDto.id`.

Errors and ApiError usage
-------------------------
Когда приходит `ApiError` и какие коды использовать (источник — код контроллеров и middleware):

- Validation errors — `400 Bad Request`.
  - `ApiError.code = "validation_error"`.
  - `fields` — объект `{"fieldName": "error message"}` где `fieldName` соответствует имени поля модели (camelCase).
  - Пример:
```json
{
  "code": "validation_error",
  "message": "Validation failed.",
  "fields": { "title": "Title is required.", "categoryId": "CategoryId is required." }
}
```

- Not found — `404 Not Found`.
  - `ApiError.code = "not_found"`.
  - Пример:
```json
{ "code": "not_found", "message": "Объявление не найдено." }
```

- Forbidden / Access denied — `403 Forbidden`.
  - `ApiError.code = "forbidden"` или специальные коды (например `post_banned`, `chat_banned`).
  - Пример (post ban):
```json
{ "code": "post_banned", "message": "You cannot create ads" }
```

- Client validation / format errors — `400 Bad Request`.
  - Пример: invalid CSV lists, unknown sort field, etc.

- Internal server error — `500 Internal Server Error`.
  - `ApiError.code = "internal_error"`.
  - `details` содержит `{ correlationId: string }` — полезно для поддержки/логов.
  - Пример:
```json
{ "code": "internal_error", "message": "Внутренняя ошибка сервера.", "details": { "correlationId": "..." } }
```

PatchResultDto semantics and typing
----------------------------------
Поля `PatchResultDto` теперь имеют camelCase и понятный смысл. Формат ответа при PATCH `/ads/{id}`:

```json
{
  "success": true,
  "updated": ["title", "mainImageId"],
  "skipped": ["someField"],
  "errors": ["Error processing image data: ..."]
}
```

- `success` — boolean, операция прошла без фатальных ошибок (не означает что все изменения применены).
- `updated` — array<string> — список имен полей (camelCase) которые были фактически изменены на сервере. Это корректно типизируется на фронте как `Array<keyof UpdateAdDto>`.
- `skipped` — array<string> — операции/поля, которые были проигнорированы (например неверный id изображения).
- `errors` — array<string> — текстовые сообщения об ошибках, которые произошли во время применения изменений.

Main image behavior (точности)
------------------------------
- При создании объявления с файлами (`POST /ads` с `files`) сервер автоматически устанавливает `Ad.MainImageId`:
  - если `mainImageIndex` передан и валиден (0 <= index < savedFilesCount) — используется соответствующий загруженный файл;
  - если `mainImageIndex` отсутствует или некорректен — выбирается индекс `0` (первый сохранённый файл).
  - Если `mainImageIndex` вне диапазона — он игнорируется и используется `0` (сервер не возвращает ошибку по этому поводу).

- После создания фронтенд получает в ответе `images: AdImageDto[]` с `id` — именно этот `id` можно использовать в `PATCH /ads/{id}` как `mainImageId` чтобы сменить главное изображение.

High-level: как создать объявление с картинками (рекомендованные сценарии)
-----------------------------------------------------------------------
Вариант A — одноцлаговый (удобнее):
1) Собрать `FormData` с полями `title`, `categoryId`, `locationId`, ... и с файлами: `formData.append("files", file)` для каждого файла.
2) (опционально) добавить `mainImageIndex` (0-based).
3) POST /ads (multipart/form-data) — в ответе при успехе получите `CreateAdResultDto` с `adId` и `images` (каждое содержит `id` и `filePath`).

Вариант B — двухшаговый (когда нужно предварительно сохранить файлы):
1) POST /ads без файлов (либо с минимальным набором полей) — получите `adId`.
2) POST /ads/{adId}/upload (multipart/form-data, поле `files`) — получите `UploadFilesResultDto.files` с относительными путями.
3) PATCH /ads/{adId}` с телом `UpdateAdDto`, где в `images` указываете объекты `{ filePath: "files/...", sortOrder: N }`. Сервер проверит наличие файла и добавит запись в `AdImages`.
4) При необходимости выставьте `mainImageId` равным id из `AdImageDto` (после PATCH, или если сервер вернул `images` при создании — используйте их `id`).

Edge cases
- Если файлы не дошли (пустые в request payload) — сервер получит `files == null` или `files.Count == 0` и вернёт `CreateAdResultDto` без `images` (или `BadRequest` если ожидались файлы при `/ads/{id}/upload`).

Chat messages: behavior and infinite scroll guidance
---------------------------------------------------
Эндпоинты сообщений поддерживают 2 режима и 2 формата:

1) JSON mode (application/json)
  - `POST /conversations/{id}/messages` с `SendMessageRequest` — для простых текстовых сообщений (Body содержит `type`, `text?`, `replyToMessageId?`).

2) Multipart mode (multipart/form-data)
  - `POST /conversations/{id}/messages` (multipart) — для отправки файлов/изображений вместе с `text`/`caption` и `replyToMessageId`.

Получение сообщений — параметры и когда какой ответ
- `GET /conversations/{id}/messages?count=10&before={messageId}`
  - non-initial load: возвращает `ConversationMessagesChunkDto` — `{ messages: [...], hasMore: bool }` с более старыми сообщениями (paging backwards). Используйте `before` равный минимальному id в текущем списке, чтобы получить предыдущую страницу (older messages).

- `GET /conversations/{id}/messages?count=10&since={messageId}`
  - возвращает все сообщения с id >= `since+1` (новые сообщения). Удобно для апдейта живого чата.

- Initial load (no `before` and no `since`):
  - Возвращает `ConversationInitialDto` (conversation meta + messages + hasMore + anchorMessageId + myLastSeenMessageId + otherLastSeenMessageId).
  - `anchorMessageId` — id последнего прочитанного сервером для текущего пользователя; используется фронтом как опорная точка: при initial load фронт может показать сообщения вокруг `anchorMessageId`, отметить read и использовать `since`/`before` для дальнейшей навигации.

Infinite scroll recipe (frontend)
1) Выполните initial load (без `before`/`since`) — получите `ConversationInitialDto` и `messages` (последние N). Сохраните `anchorMessageId` и `hasMore`.
2) Для загрузки старых сообщений (scroll up): отправляйте `GET ...?count=10&before={oldestMessageId}`; присоединяйте полученные `messages` в начало списка. Если `hasMore` false — остановитесь.
3) Для получения новых сообщений (poll/ws fallback): используйте `since={lastMessageId}` или подписку на SignalR `chat:message`/`chat:conversationUpdated`.

Примеры ответов см. блоки `ConversationInitialDto` / `ConversationMessagesChunkDto` в `docs/api.md` (они синхронизированы с DTO в `AdsPortalV2.1/Models`).


- `POST /ads` (`[Authorize]`, rate limit `CreateAdPerDay`, multipart/form-data) -> `CreateAdResultDto`
  - Request (multipart/form-data):
    - Fields (form field names must exactly match):
      - `title` (required)
      - `description` (optional)
      - `price` (optional)
      - `isNegotiable` (optional, `true`/`false`)
      - `categoryId` (required)
      - `listingType` (optional)
      - `locationId` (required)
      - `mainImageIndex` (optional, integer index into uploaded files)
    - Files: field name MUST be `files` (do NOT use `files[]`). Add each file with the same name, e.g. in JS:
      - `formData.append("files", file)` for each file
    - Do NOT set `Content-Type` header manually; let the browser include the multipart boundary.
  - Behavior (server-side):
    - Files bound to `List<IFormFile>? files` in controller. If files are present, server saves images under `wwwroot/files/{userId}/Ads/{adId}/...` and returns saved `AdImageDto[]` in response.
    - The server will set `Ad.MainImageId` automatically for created ad when files are uploaded; the chosen main image is determined by `mainImageIndex` or defaults to the first uploaded image. The frontend can also later change main image via `PATCH /ads/{id}` using `MainImageId`.
  - Response: `CreateAdResultDto`:
    - `message: string`
    - `adId: int`
    - `images?: AdImageDto[]` (present when files uploaded). Each `AdImageDto`:
      - `id: int` — use this `id` to reference the image (for setting `MainImageId` or deleting/updating images).
      - `adId: int`
      - `filePath: string` — relative path, use `"/" + filePath` to build URL or `window.location.origin + "/" + filePath` for absolute URL.
      - `sortOrder: int`

- `DELETE /ads/{id}` (`[Authorize]`) -> `CreateAdResultDto` (message + adId)

- `PATCH /ads/{id}` (`[Authorize]`) -> `PatchResultDto`
  - Request body: JSON `UpdateAdDto` (bound from body). `UpdateAdDto` (from `Models/UpdateAdDto.cs`) fields:
    - `title?: string`
    - `description?: string`
    - `price?: decimal`
    - `isNegotiable?: bool`
    - `categoryId?: int`
    - `listingType?: string`
    - `locationId?: int`
    - `mainImageId?: int` — set existing image id (from `AdImageDto.id`) to make it main image
    - `images?: UpdateAdImageDto[]` — image operations (see below)
  - `UpdateAdImageDto` (used in `images` list):
    - `id?: int` — required for delete/update of existing image
    - `delete: bool` — if true, delete image with given `id`
    - `filePath?: string` — when adding a new image already uploaded to `wwwroot` (server will validate file exists)
    - `sortOrder?: int` — optional sort order for image
  - Note: to add new image via `PATCH`, frontend must first upload file to disk (e.g. via `POST /ads/{id}/upload`) and then include the returned `filePath` in `images` array to attach it to the ad.

- `POST /ads/{id}/upload` (`[Authorize]`, multipart/form-data) -> `UploadFilesResultDto`
  - Request: multipart/form-data, files field name = `files` (one or many). Returns `UploadFilesResultDto` with `files: string[]` — relative file paths saved on server. Use these `filePath` values in `PATCH /ads/{id}` `images.FilePath` to attach uploaded files to the ad.

Notes for frontend developers (concrete guidance)
- Always send form data as `multipart/form-data` for `POST /ads` and `POST /ads/{id}/upload`.
- Use field name `files` (repeat for multiple files) — DevTools should show multiple `files: (binary)` lines in Request Payload.
- On successful `POST /ads` with files, use returned `AdImageDto[].id` values to refer to images (for `mainImageId` or deletes).
- If you upload files separately (`/ads/{id}/upload`), server returns relative paths; include those paths in `PATCH /ads/{id}` `images` array as `filePath` when attaching.


---

## AuthController (`/auth`)
- `POST /auth/register`
  - Body: `RegisterRequest` (`userLogin`, `userPassword`)
  - Response: `AuthSessionResponseDto`
- `POST /auth/login`
  - Body: `LoginRequest` (`userLogin`, `userPassword`)
  - Response: `AuthSessionResponseDto`
- `POST /auth/refresh`
  - Body: `RefreshRequest` (`refreshToken`)
  - Response: `AuthRefreshResponseDto`
- `POST /auth/logout` (`[Authorize]`) -> `200 OK`
- `POST /auth/logout-all` (`[Authorize]`) -> `200 OK`
- `GET /auth/sessions` (`[Authorize]`) -> `AuthSessionDto[]`
- `DELETE /auth/sessions/{id:guid}` (`[Authorize]`) -> `200 OK`
- `GET /me/restrictions` (`[Authorize]`)
  - Важно: абсолютный маршрут, не `/auth/me/restrictions`
  - Response: `MeRestrictionDto[]`

---

## CategoriesController (`/categories`)
- `GET /categories` -> `CategoryDto[]`
- `POST /categories` (`[Authorize]`, `CanManageCategories`)
  - Body: `UpsertCategoryDto`
  - Response: `CategoryDto`
- `PUT /categories/{id}` (`[Authorize]`, `CanManageCategories`)
  - Body: `UpsertCategoryDto`
  - Response: `CategoryDto`
- `DELETE /categories/{id}` (`[Authorize]`, `CanManageCategories`) -> `200 OK`

---

## ConversationsController (`/conversations`, `[Authorize]`)
- `POST /conversations`
  - Body: `CreateConversationRequest` (`adId`)
  - Response: `ConversationActionDto`
- `GET /conversations` -> `ConversationDto[]`
- `GET /conversations/{id:int}/messages`
  - Query: `count=10`, `before?`, `since?`
  - Response:
    - initial load (`before/since` не заданы): `ConversationInitialDto`
    - иначе: `ConversationMessagesChunkDto`
- `POST /conversations/{id:int}/messages` (`application/json`, rate limit `MessagesPerSecond`)
  - Body: `SendMessageRequest` (`type`, `text?`, `replyToMessageId?`)
  - Response: `ConversationMessageActionDto`
- `POST /conversations/{id:int}/messages` (`multipart/form-data`, rate limit `MessagesPerSecond`)
  - Form: `text?`, `caption?`, `replyToMessageId?`, `files[]`
  - Response: `ConversationMessageActionDto`
- `POST /conversations/by-ad/{adId:int}/messages` (`application/json`, rate limit `MessagesPerSecond`) -> `ConversationMessageActionDto`
- `POST /conversations/by-ad/{adId:int}/messages` (`multipart/form-data`, rate limit `MessagesPerSecond`) -> `ConversationMessageActionDto`
- `PATCH /conversations/{id:int}/read`
  - Query: `lastSeenMessageId` (required)
- `PATCH /conversations/{id:int}/mute`
- `PATCH /conversations/{id:int}/archive`
- `PATCH /conversations/{id:int}/messages/{messageId:int}`
  - Body: `EditMessageRequest` (`text?`, `attachments?`)
  - Response: `ConversationMessageActionDto`
- `POST /conversations/{id:int}/messages/{messageId:int}/attachments` (multipart/form-data)
  - Form: `files[]`
  - Response: `ConversationMessageActionDto`
- `DELETE /conversations/{id:int}/messages/{messageId:int}` -> `ConversationMessageActionDto`
- `POST /conversations/{id:int}/attachments` (multipart/form-data)
  - Form: `files[]`, `caption?`
  - Response: `ConversationMessageActionDto`
- `POST /conversations/by-ad/{adId:int}/attachments` (multipart/form-data)
  - Form: `files[]`, `caption?`
  - Response: `ConversationMessageActionDto`
- `GET /conversations/{id:int}` -> `ConversationStateDto`

---

## LocationsController (`/locations`)
- `GET /locations` -> `LocationTreeNodeDto[]`

---

## NotificationsController (`/notifications`, `[Authorize]`)
- `GET /notifications` -> `NotificationsResultDto`
  - `items: NotificationDto[]`
- `POST /notifications/read`
  - Body: `int[]?` (опционально; если пусто/null — пометить все непрочитанные)
  - Response: `200 OK`

---

## UsersController (`/users`)
- `GET /users/{id:int}` -> `UserProfileResponseDto`
- `PATCH /users/{id:int}` (`[Authorize]`)
  - Body: `Dictionary<string, object>`
  - Allowed fields:
    - `UserLogin`
    - `UserName`
    - `UserEmail`
    - `UserPhoneNumber`
    - `AvatarPath`
    - `password` (special case)
  - Response: `PatchResultDto`
- `GET /users/{id:int}/favorites` (`[Authorize]`) -> `FavoriteAdDto[]`
- `POST /users/{id:int}/favorites` (`[Authorize]`)
  - Body: `int` (`adId`)
  - Response: `FavoriteMutationDto`
- `DELETE /users/{id:int}/favorites/{adId:int}` (`[Authorize]`) -> `200 OK`
- `GET /users/{id:int}/ads` -> `UserAdDto[]`
- `GET /users/userprofile/{id:int}` -> `UserProfileDto`
- `POST /users/{id:int}/upload-avatar` (`[Authorize]`, multipart/form-data)
  - Form: `avatar`
  - Response: `AvatarUploadDto`

---

## DTO (основные)

Файл `AdsPortalV2.1/Models/ApiContracts.cs`:
- `PagedResultDto<T>`
- `AdminAdOwnerDto`, `AdminAdListItemDto`, `AdminAdsResultDto`
- `AdminUsersResultDto`, `AdminLogsResultDto`
- `AuthSessionResponseDto`, `AuthRefreshResponseDto`, `AuthSessionDto`, `MeRestrictionDto`
- `AdImageDto`, `AdCategoryDto`, `AdOwnerDto`, `AdDetailsDto`, `ModerationAdDto`
- `CreateAdResultDto`, `UploadFilesResultDto`, `PatchResultDto`
- `FavoriteMutationDto`, `AvatarUploadDto`
- `RestrictionActionDto`, `RestrictionTypeDto`, `RoleActionDto`, `AdStatusActionDto`
- `NotificationDto`, `NotificationsResultDto`
- `UserAdDto`, `UserProfileDto`, `UserProfileResponseDto`, `FavoriteAdDto`
- `ConversationActionDto`, `ConversationMessageActionDto`
- `ConversationAdMetaDto`, `ConversationUserPresenceDto`, `ConversationMetaDto`
- `MessageAuthorDto`, `ConversationMessageDto`, `ConversationMessagesChunkDto`, `ConversationInitialDto`, `ConversationStateDto`
- `AdminAuditLogDto`

Файл `AdsPortalV2.1/Models/ConversationDto.cs`:
- `ConversationDto`
- `ConversationCompanionDto`
- `ConversationAdDto`
- `ConversationLastMessageDto`

Файл `AdsPortalV2.1/Models/ChatMessage.cs`:
- `ChatMessage`
- `ChatAttachment`

Файл `AdsPortalV2.1/Models/ApiError.cs`:
- `ApiError`: `code`, `message`, `fields?`, `details?`

---

## SignalR

### `NotificationHub`
Methods:
- `JoinConversation(int conversationId)`
- `LeaveConversation(int conversationId)`
- `RequestNotifications()`

Server events:
- `initNotifications`
- (из сервисов/контроллеров в те же каналы отправляются) `chat:conversationCreated`, `chat:message`, `chat:conversationUpdated`, `chat:read`, `chat:typing`, `chat:onlineUsers`

### `OnlineHub`
Methods:
- `Ping()`
- `JoinGroup(int conversationId)`
- `LeaveGroup(int conversationId)`
- `Read(int conversationId, int lastSeenMessageId)`
- `Typing(int conversationId)`

Server events:
- `chat:read`
- `chat:typing`
- `chat:onlineUsers`

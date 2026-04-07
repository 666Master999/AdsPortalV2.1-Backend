# API contract — текущий контракт (по коду)

Источник истины: код проекта `AdsPortalV2.1`.

---

## Общие правила
- JSON сериализуется в camelCase.
- Enum сериализуются как camelCase string (`JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`).
  - Пример: `LocationType.Region` -> `"region"`, `AdStatus.PendingModeration` -> `"pendingModeration"`.
- Для `GET /ads`:
  - `page` нормализуется: `>= 1`.
  - `pageSize` clamp: `1..50`.
  - `location` (CSV int) — максимум 10 id.
  - `sort`: whitelist `title, price, createdAt, updatedAt, views, favorites`.
  - default sort: `-createdAt`.
- `dateFrom` / `dateTo` в query: `DateOnly` (`YYYY-MM-DD`).
  - `dateTo` inclusive в бизнес-логике (`< dateTo + 1 day`).

---

## GET /locations
Возвращает дерево локаций.

Response: `LocationTreeNodeDto[]`
- `id: int`
- `name: string`
- `type: "region" | "city" | "district"`
- `children: LocationTreeNodeDto[]`

---

## AdsController (`/ads`)

### PATCH /ads/{id}/moderation
- Auth: policy `CanModerateAd`
- Body: `AdStatus` (enum string)
- Response: `AdDto`

### GET /ads/moderation
- Auth: policy `CanModerateAd`
- Response: `ModerationAdDto[]`

### GET /ads
Поиск/листинг объявлений.

Query (`AdsQuery`):
- `search: string?`
- `location: string?` (CSV int)
- `category: string?` (CSV int)
- `priceFrom: decimal?`
- `priceTo: decimal?`
- `dateFrom: YYYY-MM-DD`
- `dateTo: YYYY-MM-DD`
- `userId: int?`
- `status: active|pendingModeration|rejected|deleted` (case-insensitive)
- `type: string?` (фильтр по `Ad.ListingType`)
- `page: int` (default 1)
- `pageSize: int` (default 20, clamp `1..50`)
- `sort: string?` (`-field` = desc)

Response: `PagedResultDto<AdListItemDto>`
- `items: AdListItemDto[]`
- `total: int`
- `page: int`
- `pageSize: int`
- `totalPages: int` (если `total == 0`, возвращается `1`)

`AdListItemDto`:
- `id: int`
- `title: string`
- `description: string?`
- `price: decimal?`
- `isNegotiable: bool`
- `categoryId: int?`
- `locationId: int` (**не nullable**)
- `location: { type, id, name } | null` (`LocationRef`)
- `listingType: string?`
- `createdAt: datetime`
- `updatedAt: datetime`
- `userId: int`
- `viewsCount: int`
- `favoritesCount: int`
- `mainImageUrl: string?`
- `isFavorite: bool`
- `moderationStatus: "active"|"pendingModeration"|"rejected"|"deleted"|null`

### GET /ads/{id}
Возвращает `AdDetailsDto`.
- Если объявление не найдено: `404` + `ApiError(code="not_found")`
- Если нет доступа: `403` + `ApiError(code="forbidden")`

`AdDetailsDto`:
- `id, userId, categoryId, title, description, price, isNegotiable`
- `locationId: int`
- `location: LocationRef?`
- `listingType`
- `createdAt, updatedAt`
- `moderationStatus: AdStatus?` (только для владельца/привилегированного)
- `rejectionReason: string?` (только для владельца/привилегированного)
- `deletedAt: datetime?`
- `category: AdCategoryDto?`
- `user: AdOwnerDto?`
- `images: AdImageDto[]`
- `isFavorite: bool`

### POST /ads
- Auth required
- Rate limit: `CreateAdPerDay`
- Content-Type: `multipart/form-data`
- Form:
  - `title` (required)
  - `description`
  - `price`
  - `isNegotiable`
  - `categoryId` (required)
  - `listingType`
  - `locationId` (required)
  - `files[]` (optional)
  - `mainImageIndex` (optional)

Response: `CreateAdResultDto`
- `message: string`
- `adId: int`
- `images?: AdImageDto[]`

### DELETE /ads/{id}
- Auth required
- Response: `CreateAdResultDto` (`message`, `adId`)

### PATCH /ads/{id}
- Auth required
- Body: `UpdateAdDto`
- Response: `PatchResultDto`
  - `success: bool`
  - `updated: string[]`
  - `skipped: string[]`
  - `errors: string[]`

// removed: GET /ads/{id}/is-favorite — isFavorite now returned in `GET /ads` as `isFavorite: bool`

### POST /ads/{id}/upload
- Auth required
- Body: `multipart/form-data`, `files[]`
- Response: `UploadFilesResultDto` (`files: string[]`)

---

## Формат ошибок (`ApiError`)
Файл: `AdsPortalV2.1/Models/ApiError.cs`

Поля:
- `code: string`
- `message: string`
- `fields?: { [key: string]: string }` — для validation ошибок
- `details?: object` — для внутренних деталей (например `correlationId`)

Примеры:

Validation:
```json
{
  "code": "validation_error",
  "message": "Validation failed.",
  "fields": {
    "Title": "Title is required.",
    "CategoryId": "CategoryId is required.",
    "LocationId": "LocationId is required."
  }
}
```

Internal:
```json
{

// Примечание: тип ограничения `ChatBan` заменяет прежний `CommentBan`.
// При блокировке чата сервер возвращает ошибку с кодом `chat_banned` для операций чата.
}

```


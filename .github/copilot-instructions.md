# Copilot Instructions

## Project Guidelines
- Всегда отвечать пользователю на русском языке.
- Ensure that the frontend performs basic validation for UX, while the backend executes final validation and serves as the source of truth. В случае некорректных данных, например, если в поле 'price' приходит строка вместо числа, поле не обновляется в базе данных, и клиенту отправляется сообщение о том, что поле не было обновлено из-за некорректного заполнения.
- Avoid adding unnecessary checks or validations in the code if the functionality can work correctly without them. Всегда искать простые и современные решения, избегая дополнительных конструкций, если это не требуется. Пользователь предпочитает минималистичные и короткие решения для кода; избегать раздутых реализаций, по возможности укладываться в компактные, простые изменения без лишних проверок и усложнений.
- Write minimalistic and aesthetically pleasing code with clean and concise implementations, emphasizing clarity and elegance over extreme brevity or one-liner constructs. Reduce the number of symbols and lines of code, and avoid repetitive or verbose constructs like multiple 'if' statements.
- Always double-check when refactoring or moving code to ensure no duplication occurs. Avoid creating redundant copies of logic during refactoring.
- Всегда самостоятельно убеждаться в корректности решений, проверять данные и логику, а не перекладывать ответственность на пользователя.
- Не добавлять новые свойства в классы, а использовать существующие данные или находить решения без изменения структуры классов.
- В проекте для локации объявлений нужно использовать единую модель через `CityId`/`DistrictId` и навигационные свойства, без зависимости от легаси-поля `Ad.City` в логике.

## Chat Functionality Guidelines
- Для чатов `unreadCount` является единственным источником правды с бэкенда: фронт не должен увеличивать, уменьшать или рассчитыать `unread`, а только отображать значения из `chat:conversationUpdated`.
- Для чатов всегда использовать последнее событие по времени (`lastMessageAt`) как актуальное, чтобы избегать race condition и устаревших обновлений.
- Прочтение сообщений работает через `lastReadMessageId`, который хранится на бэкенде.
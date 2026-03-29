# Copilot Instructions

## Project Guidelines
- В проекте используется ключ "id" для идентификатора пользователя в JWT-токенах вместо стандартного ClaimTypes.NameIdentifier. Это нужно учитывать при работе с авторизацией.
- Всегда отвечать пользователю на русском языке.
- Ensure that the frontend sends clean, valid requests to the backend, avoiding unnecessary validations or error handling for invalid data on the frontend. В случае некорректных данных, например, если в поле 'price' приходит строка вместо числа, поле не обновляется в базе данных, и клиенту отправляется сообщение о том, что поле не было обновлено из-за некорректного заполнения.
- Avoid adding unnecessary checks or validations in the code if the functionality can work correctly without them. Всегда искать простые и современные решения, избегая дополнительных конструкций, если это не требуется. Пользователь предпочитает минималистичные и короткие решения для кода; избегать раздутых реализаций, по возможности укладываться в компактные, простые изменения без лишних проверок и усложнений.
- Write minimalistic and aesthetically pleasing code with clean and concise implementations, emphasizing clarity and elegance over extreme brevity or one-liner constructs. Reduce the number of symbols and lines of code, and avoid repetitive or verbose constructs like multiple 'if' statements.
- Always double-check when refactoring or moving code to ensure no duplication occurs. Avoid creating redundant copies of logic during refactoring.
- Всегда самостоятельно убеждаться в корректности решений, проверять данные и логику, а не перекладывать ответственность на пользователя.
- Не добавлять новые свойства в классы, а использовать существующие данные или находить решения без изменения структуры классов.
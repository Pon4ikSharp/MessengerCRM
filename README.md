# MessengerCRM — система роботи з повідомленнями

Серверний застосунок на C# / ASP.NET Core для роботи з Telegram та WhatsApp. Вихідний код міститься у `src/MessengerCRM.Server/`, а окремий компонент WhatsApp — у `wa-engine/`.

## Що в репозиторії

- `src/MessengerCRM.Server/` — вихідний код C# і вебінтерфейс;
- `wa-engine/` — Node.js-компонент інтеграції WhatsApp;
- `.gitignore` — виключення баз даних, токенів, сесій та збірок.

## Збірка і запуск

1. Встановити .NET SDK 10 і Node.js (версії, сумісні з пакунками).
2. Із кореня репозиторію запустити `dotnet restore src/MessengerCRM.Server/MessengerCRM.Server.csproj`.
3. Запустити сервер: `dotnet run --project src/MessengerCRM.Server/MessengerCRM.Server.csproj`.
4. Для компонента WhatsApp перейти в `wa-engine`, виконати `npm install` і налаштувати запуск `server.mjs` за вимогами середовища.
5. Виконати первинне налаштування та авторизацію месенджерів за допомогою вебінтерфейсу.

**Примітка:** із публічної копії навмисно вилучено серверну SQLite-базу, файли Telegram/WhatsApp-сесій, API-токени й готову виконувану збірку. C# код створює структуру локальної бази для нового екземпляра. Без чинної авторизації зовнішні інтеграції не працюватимуть. Повний запуск цієї очищеної версії не тестувався.

Ніколи не завантажуйте в публічний репозиторій `data/`, файли `*.session`, `*.db`, `api-token.txt`, `engine-token.txt`, `secrets.json` або WhatsApp-профілі.

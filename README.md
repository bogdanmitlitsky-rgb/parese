# Hotline Parser

WPF-программа для парсинга цен Hotline и обновления Google Sheets.

## Что хранится в Git

В репозиторий добавляется только исходный код и безопасные примеры настроек.
Реальные токены Telegram, Google service account, прокси, логи, архивы и сборки не добавляются.

## Первый запуск после клонирования

1. Установить .NET SDK 8 для Windows.
2. Скопировать `Hotline-Main-Parsing/appsettings.example.json` в `Hotline-Main-Parsing/appsettings.json`.
3. Скопировать `Hotline-Main-Parsing/credentialsServiceAccount.example.json` в `Hotline-Main-Parsing/credentialsServiceAccount.json` и вставить реальные данные Google service account.
4. Скопировать `Hotline-Main-Parsing/proxy_default.example.txt` в `Hotline-Main-Parsing/proxy_default.txt`.
5. Скопировать `Hotline-Main-Parsing/proxy_aks.example.txt` в `Hotline-Main-Parsing/proxy_aks.txt`.
6. Заполнить Telegram token, chat ids, Google Sheet ids и прокси.
7. Собрать проект:

```powershell
dotnet build .\Hotline-Main-Parsing.sln
```

## Публикация

```powershell
dotnet publish .\Hotline-Main-Parsing\Hotline-Main-Parsing.csproj -c Release -o .\publish\HotlineParser
```

Папка `publish/` не хранится в Git.

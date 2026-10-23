# uqoz

Мессенджер на F# + Avalonia. Аккаунты и сообщения — Firebase (Authentication + Realtime Database).

## Что работает

- регистрация: ник + почта + пароль (Firebase Auth, Email/Password);
- вход по почте и паролю, токен автоматически обновляется;
- уникальность ника (проверка + правило `!data.exists()` в базе);
- список пользователей и личные диалоги;
- сообщения шифруются AES-256 перед отправкой, в базе только шифротекст;
- автообновление переписки раз в 2 секунды;
- тёмная (полностью чёрная) и светлая темы + режим «как в системе»;
- боковая панель с переключателем **К / ЛС** ↔ **Аккаунт / Настройки**.

Сквозной тест (регистрация → вход → отправка → чтение вторым аккаунтом → расшифровка) проходит.

## Запуск

```powershell
cd D:\builder\vsinsiders\uqoz
dotnet run --project uqoz/uqoz.fsproj
```

Нужен .NET 10 SDK.

## Настройка Firebase

Проект: `uqoz-cazu`.

### 1. Web API key

Ключ берётся из одного из источников:

1. переменная окружения `UQOZ_FIREBASE_API_KEY`;
2. файл `uqoz/firebase.key` рядом с `.fsproj` (копируется в папку сборки, добавлен в `.gitignore`);
3. иначе приложение покажет ошибку «Не задан Firebase Web API key».

```powershell
setx UQOZ_FIREBASE_API_KEY "AIza..."
```

URL базы по умолчанию — `https://uqoz-cazu-default-rtdb.europe-west1.firebasedatabase.app`, переопределяется через `UQOZ_FIREBASE_URL`.

### 2. Включить вход

Firebase Console → **Authentication → Sign-in method → Email/Password → Enable**.

### 3. Правила Realtime Database

```
{
  "rules": {
    ".read": false,
    ".write": false,
    "users": {
      ".read": "auth != null",
      "$uid": {
        ".write": "auth != null && auth.uid == $uid"
      }
    },
    "nicks": {
      ".read": "auth != null",
      "$nick": {
        ".write": "auth != null && ((!data.exists() && newData.val() == auth.uid) || (data.val() == auth.uid && !newData.exists()))"
      }
    },
    "messages": {
      "$chatId": {
        ".read": "auth != null && ($chatId.beginsWith(auth.uid + '__') || $chatId.endsWith('__' + auth.uid))",
        ".write": "auth != null && ($chatId.beginsWith(auth.uid + '__') || $chatId.endsWith('__' + auth.uid))"
      }
    }
  }
}
```

Без этих правил база открыта: любой, кто знает URL, может читать и менять данные.

## Данные

```
users/{uid}                 { nick, email, avatar, createdAt }
nicks/{ник}                 uid
messages/{uidA__uidB}/{id}  { from, fromNick, cipher, iv, ts }
```

`chatId` — uid участников, отсортированные и склеенные через `__`.
Ключ шифрования чата: `SHA-256(chatId + секрет приложения)`.

## Ограничения шифрования

Пароли обрабатывает Firebase. Сообщения — AES-256-CBC, IV хранится рядом с шифротекстом.
Секрет приложения зашит в клиент, поэтому это **шифрование на уровне приложения, а не E2EE**: у кого есть бинарник и доступ к базе, тот может расшифровать. Для настоящего E2EE нужен обмен ключами (X3DH / Double Ratchet).

## Структура

```
uqoz/
  Crypto.fs                     AES (Encryption + CryptoService)
  Firebase.fs                   Auth REST + Realtime Database REST
  ViewModels/MainViewModel.fs   состояние, команды, темы, опрос
  Views/MainWindow.axaml        интерфейс
  App.axaml                     темы и стили
  Db.fs, AuthService.fs, …      старый слой SQLite/EF Core (не используется)
```

Старые `Db.fs`, `AuthService.fs`, `ChatService.fs`, `FriendService.fs`, `UserSettingsService.fs` оставлены как история и не подключены к UI. Их можно удалить вместе с пакетами EF Core/SQLite, когда не понадобятся.

## Дальше

- realtime через SSE вместо опроса;
- каналы (вкладка К) и friends-список;
- звонки (сейчас макет; в зависимостях есть SIPSorcery);
- аватары и статусы.

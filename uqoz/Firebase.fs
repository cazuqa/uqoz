namespace uqoz.Firebase

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open uqoz.Crypto

/// Настройки подключения.
/// URL и Web API key можно задать переменными окружения,
/// чтобы не хранить их в исходниках.
module FirebaseConfig =

    let defaultUrl = "https://uqoz-cazu-default-rtdb.europe-west1.firebasedatabase.app"

    /// Web API key: Firebase Console → Project settings → General → Web API Key.
    /// Пока не вставлен — приложение честно скажет об этом при входе.
    let apiKeyPlaceholder = "PASTE_FIREBASE_WEB_API_KEY"

    let dbUrl () =
        match Environment.GetEnvironmentVariable "UQOZ_FIREBASE_URL" with
        | null | "" -> defaultUrl
        | value -> value.Trim().TrimEnd('/')

    let apiKey () =
        // 1) переменная окружения
        match Environment.GetEnvironmentVariable "UQOZ_FIREBASE_API_KEY" with
        | value when not (String.IsNullOrWhiteSpace value) -> value.Trim()
        | _ ->
            // 2) локальный файл рядом с exe (добавлен в .gitignore)
            try
                let path = System.IO.Path.Combine(AppContext.BaseDirectory, "firebase.key")
                if System.IO.File.Exists path then
                    System.IO.File.ReadAllText(path).Trim()
                else
                    apiKeyPlaceholder
            with _ -> apiKeyPlaceholder

    let isConfigured () = apiKey () <> apiKeyPlaceholder


type Session = {
    Uid: string
    Email: string
    Nick: string
    IdToken: string
    RefreshToken: string
    ExpiresAt: DateTime
}

type UserProfile = {
    Uid: string
    Nick: string
    Email: string
    Avatar: string
} with
    member this.Initial =
        if String.IsNullOrWhiteSpace this.Nick then "?" else this.Nick.Substring(0, 1).ToUpperInvariant()

type ChatMessage = {
    Id: string
    FromUid: string
    FromNick: string
    Text: string
    Timestamp: DateTime
}

// ---------------------------------------------------------------------------
// JSON helpers
// ---------------------------------------------------------------------------

module private Json =

    let str (element: JsonElement) (name: string) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
        | _ -> ""

    let number (element: JsonElement) (name: string) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Number ->
            match value.TryGetInt64() with
            | true, n -> n
            | _ -> 0L
        | _ -> 0L

    let escape (value: string) = JsonSerializer.Serialize value

    /// Бросает исключение и подходит под любой ожидаемый тип.
    let fail (message: string) : 'a = raise (Exception message)

    /// Часть адреса до @ — запасной ник, если профиль не найден.
    let emailNick (email: string) =
        let at = email.IndexOf '@'
        if at > 0 then email.Substring(0, at).ToLowerInvariant() else "user"

    let errorCode (text: string) =
        try
            use doc = JsonDocument.Parse text
            let mutable error = Unchecked.defaultof<JsonElement>

            if doc.RootElement.TryGetProperty("error", &error)
               && error.TryGetProperty("message", &error) then
                error.GetString()
            else
                ""
        with _ -> ""

    let translate (code: string) =
        match code with
        | "EMAIL_EXISTS" -> "Эта почта уже зарегистрирована"
        | "EMAIL_NOT_FOUND" -> "Аккаунт с такой почтой не найден"
        | "INVALID_PASSWORD" -> "Неверный пароль"
        | "INVALID_LOGIN_CREDENTIALS" -> "Неверная почта или пароль"
        | "INVALID_EMAIL" -> "Некорректная почта"
        | "MISSING_EMAIL" -> "Введите почту"
        | "MISSING_PASSWORD" -> "Введите пароль"
        | "WEAK_PASSWORD" -> "Пароль слишком простой: минимум 6 символов"
        | "PASSWORD_DOES_NOT_MEET_REQUIREMENTS" -> "Пароль не соответствует требованиям Firebase"
        | "TOO_MANY_ATTEMPTS_TRY_LATER" -> "Слишком много попыток, попробуйте позже"
        | "USER_DISABLED" -> "Аккаунт отключён"
        | "CONFIGURATION_NOT_FOUND"
        | "OPERATION_NOT_ALLOWED" ->
            "В Firebase не включён вход Email/Password. Authentication → Sign-in method → Email/Password → Enable"
        | "API_KEY_INVALID" -> "Неверный Firebase Web API key"
        | "PERMISSION_DENIED" -> "Нет доступа: проверьте правила Realtime Database"
        | "" -> "Неизвестная ошибка Firebase"
        | other -> other

// ---------------------------------------------------------------------------
// Firebase client: Auth (Identity Toolkit) + Realtime Database (REST)
// ---------------------------------------------------------------------------

type FirebaseClient(apiKey: string, dbUrl: string) =

    let http = new HttpClient(Timeout = TimeSpan.FromSeconds 25.0)
    let root = dbUrl.Trim().TrimEnd('/')
    let mutable session: Session option = None

    // --- HTTP helpers -------------------------------------------------------

    member private _.SendRawAsync(url: string, payload: string, method: HttpMethod) : Task<int * string> = task {
        use request = new HttpRequestMessage(method, url)

        if not (String.IsNullOrEmpty payload) then
            request.Content <- new StringContent(payload, Encoding.UTF8, "application/json")

        let! response = http.SendAsync request
        let! body = response.Content.ReadAsStringAsync()
        return int response.StatusCode, body
    }

    member private this.TokenEndpoint(host: string, path: string) =
        sprintf "https://%s/v1/%s?key=%s" host path (Uri.EscapeDataString apiKey)

    // --- session ------------------------------------------------------------

    member this.Session = session
    member this.IsSignedIn = session.IsSome
    member this.Uid = session |> Option.map (fun s -> s.Uid) |> Option.defaultValue ""
    member this.Nick = session |> Option.map (fun s -> s.Nick) |> Option.defaultValue ""

    member this.SignOut() = session <- None

    member private this.ApplyAuthResponse(email: string) : JsonDocument -> Session =
        fun doc ->
            let rootEl = doc.RootElement
            let expiresIn = Json.number rootEl "expiresIn"

            {
                Uid = Json.str rootEl "localId"
                Email = (let e = Json.str rootEl "email" in if e = "" then email else e)
                Nick = ""
                IdToken = Json.str rootEl "idToken"
                RefreshToken = Json.str rootEl "refreshToken"
                ExpiresAt = DateTime.UtcNow.AddSeconds(if expiresIn > 0L then float expiresIn else 3600.0)
            }

    member private this.RefreshTokenAsync(current: Session) : Task<Session> = task {
        if String.IsNullOrEmpty apiKey || apiKey = FirebaseConfig.apiKeyPlaceholder then
            failwith "Не задан Firebase Web API key"

        let url =
            sprintf
                "https://securetoken.googleapis.com/v1/token?key=%s"
                (Uri.EscapeDataString apiKey)

        use content =
            new FormUrlEncodedContent(
                [ KeyValuePair("grant_type", "refresh_token")
                  KeyValuePair("refresh_token", current.RefreshToken) ]
            )

        let! response = http.PostAsync(url, content)
        let! text = response.Content.ReadAsStringAsync()

        if not response.IsSuccessStatusCode then
            session <- None
            failwith "Сессия истекла — войдите заново"

        use doc = JsonDocument.Parse text
        let rootEl = doc.RootElement
        let refresh = Json.str rootEl "refresh_token"
        let expiresIn = Json.number rootEl "expires_in"

        let updated = {
            current with
                IdToken = Json.str rootEl "id_token"
                RefreshToken = (if refresh = "" then current.RefreshToken else refresh)
                ExpiresAt = DateTime.UtcNow.AddSeconds(if expiresIn > 0L then float expiresIn else 3600.0)
        }

        session <- Some updated
        return updated
    }

    member private this.EnsureTokenAsync() : Task<string> = task {
        match session with
        | None -> return Json.fail "Вы не вошли в аккаунт"
        | Some current when current.ExpiresAt > DateTime.UtcNow.AddMinutes 2.0 ->
            return current.IdToken
        | Some current ->
            let! updated = this.RefreshTokenAsync current
            return updated.IdToken
    }

    /// Запрос к Realtime Database с токеном; при 401 токен обновляется
    /// и запрос повторяется один раз.
    member private this.DbAsync(path: string, method: HttpMethod, payload: string option) : Task<string> = task {
        let build (token: string) =
            let url =
                sprintf
                    "%s/%s.json?auth=%s"
                    root
                    (path.Trim('/'))
                    (Uri.EscapeDataString token)

            let request = new HttpRequestMessage(method, url)
            match payload with
            | Some json -> request.Content <- new StringContent(json, Encoding.UTF8, "application/json")
            | None -> ()
            request

        let! token = this.EnsureTokenAsync()
        let request = build token
        let! response = http.SendAsync request
        let! body = response.Content.ReadAsStringAsync()
        request.Dispose()

        if response.StatusCode = HttpStatusCode.Unauthorized then
            match session with
            | None -> return Json.fail "Вы не вошли в аккаунт"
            | Some current ->
                let! updated = this.RefreshTokenAsync current
                let retry = build updated.IdToken
                let! retryResponse = http.SendAsync retry
                let! retryBody = retryResponse.Content.ReadAsStringAsync()
                retry.Dispose()

                if not retryResponse.IsSuccessStatusCode then
                    return Json.fail (sprintf "Firebase: HTTP %d — %s" (int retryResponse.StatusCode) retryBody)
                else
                    return retryBody
        elif not response.IsSuccessStatusCode then
            return Json.fail (sprintf "Firebase: HTTP %d — %s" (int response.StatusCode) body)
        else
            return body
    }
    // --- Auth ---------------------------------------------------------------

    member private this.AuthRequestAsync(path: string, payload: string) : Task<Result<Session, string>> = task {
        if String.IsNullOrWhiteSpace apiKey || apiKey = FirebaseConfig.apiKeyPlaceholder then
            return Result.Error "Не задан Firebase Web API key (UQOZ_FIREBASE_API_KEY)"
        else
            let url = this.TokenEndpoint("identitytoolkit.googleapis.com", path)
            let! status, text = this.SendRawAsync(url, payload, HttpMethod.Post)

            if status <> 200 then
                return Result.Error(Json.translate (Json.errorCode text))
            else
                use doc = JsonDocument.Parse text
                return Ok(this.ApplyAuthResponse "" doc)
    }

    member private this.DeleteAccountAsync(idToken: string) : Task = task {
        let url = this.TokenEndpoint("identitytoolkit.googleapis.com", "accounts:delete")
        let payload = sprintf """{"idToken":%s}""" (Json.escape idToken)
        let! _ = this.SendRawAsync(url, payload, HttpMethod.Post)
        return ()
    }

    member private this.LoadProfileAsync(uid: string) : Task<Option<JsonElement>> = task {
        let! raw = this.DbAsync("users/" + uid, HttpMethod.Get, None)

        if String.IsNullOrWhiteSpace raw || raw.Trim() = "null" then
            return None
        else
            use doc = JsonDocument.Parse raw
            return Some(doc.RootElement.Clone())
    }

    member this.SignUpAsync(nick: string, email: string, password: string) : Task<Result<Session, string>> = task {
        let cleanNick = nick.Trim().TrimStart('@').ToLowerInvariant()
        let cleanEmail = email.Trim()

        if cleanNick.Length < 3 || cleanNick.Length > 20 then
            return Result.Error "Ник: от 3 до 20 символов"
        elif not (cleanNick |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_')) then
            return Result.Error "В нике допустимы буквы, цифры и _"
        elif String.IsNullOrWhiteSpace cleanEmail then
            return Result.Error "Введите почту"
        elif String.IsNullOrWhiteSpace password || password.Length < 6 then
            return Result.Error "Пароль должен быть не короче 6 символов"
        else
            let signUpPayload =
                sprintf
                    """{"email":%s,"password":%s,"returnSecureToken":true}"""
                    (Json.escape cleanEmail)
                    (Json.escape password)

            let! authResult =
                this.AuthRequestAsync("accounts:signUp", signUpPayload)

            match authResult with
            | Result.Error message -> return Result.Error message
            | Ok auth ->
                let withNick = { auth with Nick = cleanNick }
                session <- Some withNick

                let rollback (message: string) : Task<Result<Session, string>> = task {
                    do! this.DeleteAccountAsync withNick.IdToken
                    session <- None
                    return Result.Error message
                }

                let! existing =
                    task {
                        try
                            let! value = this.DbAsync("nicks/" + cleanNick, HttpMethod.Get, None)
                            return Ok(not (String.IsNullOrWhiteSpace value) && value.Trim() <> "null")
                        with ex -> return Result.Error ex.Message
                    }

                match existing with
                | Result.Error message ->
                    // База не ответила: закрытые правила или нет сети.
                    return! rollback ("Не удалось проверить ник: " + message)
                | Ok true -> return! rollback "Этот ник уже занят"
                | Ok false ->
                    try
                        // Ник закрепляем за uid: правило базы не даст перезаписать занятый.
                        let! _ =
                            this.DbAsync("nicks/" + cleanNick, HttpMethod.Put, Some(Json.escape withNick.Uid))

                        let profile = JsonObject()
                        profile["nick"] <- JsonValue.Create cleanNick
                        profile["email"] <- JsonValue.Create cleanEmail
                        profile["avatar"] <- JsonValue.Create ""
                        profile["createdAt"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())

                        let! _ =
                            this.DbAsync(
                                "users/" + withNick.Uid,
                                HttpMethod.Put,
                                Some(profile.ToJsonString())
                            )

                        return Ok withNick
                    with ex ->
                        return! rollback ("Не удалось создать профиль: " + ex.Message)
    }

    member this.SignInAsync(email: string, password: string) : Task<Result<Session, string>> = task {
        if String.IsNullOrWhiteSpace email || String.IsNullOrWhiteSpace password then
            return Result.Error "Введите почту и пароль"
        else
            let payload =
                sprintf
                    """{"email":%s,"password":%s,"returnSecureToken":true}"""
                    (Json.escape (email.Trim()))
                    (Json.escape password)

            let! authResult =
                this.AuthRequestAsync("accounts:signInWithPassword", payload)

            match authResult with
            | Result.Error message -> return Result.Error message
            | Ok auth ->
                session <- Some auth

                let! profile = this.LoadProfileAsync auth.Uid

                match profile with
                | Some node ->
                    let nick = Json.str node "nick"
                    let named = { auth with Nick = (if nick = "" then Json.emailNick auth.Email else nick) }
                    session <- Some named
                    return Ok named
                | None ->
                    // Аккаунт Firebase есть, а профиля нет — создаём минимальный.
                    let fallbackNick = Json.emailNick auth.Email
                    let profileJson = JsonObject()
                    profileJson["nick"] <- JsonValue.Create fallbackNick
                    profileJson["email"] <- JsonValue.Create auth.Email
                    profileJson["avatar"] <- JsonValue.Create ""
                    profileJson["createdAt"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())

                    let! _ =
                        this.DbAsync(
                            "users/" + auth.Uid,
                            HttpMethod.Put,
                            Some(profileJson.ToJsonString())
                        )

                    let named = { auth with Nick = fallbackNick }
                    session <- Some named
                    return Ok named
    }

    // --- users --------------------------------------------------------------

    /// Разбор узла users — общий для REST-запроса и для потока.
    member this.ParseUsers(raw: string) : UserProfile list =
        if String.IsNullOrWhiteSpace raw || raw.Trim() = "null" then
            []
        else
            use doc = JsonDocument.Parse raw

            match doc.RootElement.ValueKind with
            | JsonValueKind.Object ->
                doc.RootElement.EnumerateObject()
                |> Seq.choose (fun property ->
                    let node = property.Value
                    if node.ValueKind <> JsonValueKind.Object then
                        None
                    else
                        let nick = Json.str node "nick"
                        Some {
                            Uid = property.Name
                            Nick = (if nick = "" then property.Name else nick)
                            Email = Json.str node "email"
                            Avatar = Json.str node "avatar"
                        })
                |> Seq.sortBy (fun u -> u.Nick)
                |> Seq.toList
            | _ -> []

    member this.UsersAsync() : Task<UserProfile list> = task {
        let! raw = this.DbAsync("users", HttpMethod.Get, None)
        return this.ParseUsers raw
    }

    /// Удаление своего аккаунта: освобождает ник, стирает профиль
    /// и удаляет пользователя в Firebase Authentication.
    member this.DeleteMyAccountAsync() : Task<Result<unit, string>> = task {
        match session with
        | None -> return Result.Error "Вы не вошли в аккаунт"
        | Some current ->
            try
                // Ник освобождаем первым: после accounts:delete токен уже недействителен.
                if not (String.IsNullOrWhiteSpace current.Nick) then
                    try
                        let! _ = this.DbAsync("nicks/" + current.Nick, HttpMethod.Delete, None)
                        ()
                    with ex ->
                        System.Diagnostics.Debug.WriteLine("nick release: " + ex.Message)

                try
                    let! _ = this.DbAsync("users/" + current.Uid, HttpMethod.Delete, None)
                    ()
                with ex ->
                    System.Diagnostics.Debug.WriteLine("profile delete: " + ex.Message)

                do! this.DeleteAccountAsync current.IdToken
                session <- None
                return Ok()
            with ex -> return Result.Error("Не удалось удалить аккаунт: " + ex.Message)
    }

    // --- chats --------------------------------------------------------------

    member private _.ChatKey(id: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes(id + "|uqoz-chat-v1-7f3a9c2e"))

    member this.ChatIdWith(peerUid: string) =
        let me = this.Uid
        if String.CompareOrdinal(me, peerUid) <= 0 then me + "__" + peerUid else peerUid + "__" + me

    member this.SendAsync(peerUid: string, text: string) : Task<Result<unit, string>> = task {
        match session with
        | None -> return Result.Error "Вы не вошли в аккаунт"
        | Some current when String.IsNullOrWhiteSpace text ->
            return Result.Error "Пустое сообщение"
        | Some current ->
            let chat = this.ChatIdWith peerUid
            let cipher, iv = CryptoService.Encrypt text (this.ChatKey chat)

            let payload = JsonObject()
            payload["from"] <- JsonValue.Create current.Uid
            payload["fromNick"] <- JsonValue.Create current.Nick
            payload["cipher"] <- JsonValue.Create cipher
            payload["iv"] <- JsonValue.Create(Convert.ToBase64String iv)
            payload["ts"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())

            try
                let! _ =
                    this.DbAsync(
                        "messages/" + chat,
                        HttpMethod.Post,
                        Some(payload.ToJsonString())
                    )

                return Ok()
            with ex ->
                return Result.Error ex.Message
    }

    /// Разбор узла messages/{chatId} — общий для REST-запроса и для потока.
    member this.ParseMessages(peerUid: string, raw: string) : ChatMessage list =
        if String.IsNullOrWhiteSpace raw || raw.Trim() = "null" then
            []
        else
            use doc = JsonDocument.Parse raw

            match doc.RootElement.ValueKind with
            | JsonValueKind.Object ->
                let key = this.ChatKey(this.ChatIdWith peerUid)

                doc.RootElement.EnumerateObject()
                |> Seq.choose (fun property ->
                    let node = property.Value
                    if node.ValueKind <> JsonValueKind.Object then
                        None
                    else
                        try
                            let ts =
                                DateTimeOffset
                                    .FromUnixTimeMilliseconds(Json.number node "ts")
                                    .LocalDateTime

                            let cipher = Json.str node "cipher"
                            let iv = Json.str node "iv"

                            let text =
                                try
                                    CryptoService.Decrypt cipher key (Convert.FromBase64String iv)
                                with _ -> "[не удалось расшифровать]"

                            Some {
                                Id = property.Name
                                FromUid = Json.str node "from"
                                FromNick = Json.str node "fromNick"
                                Text = text
                                Timestamp = ts
                            }
                        with _ -> None)
                |> Seq.sortBy (fun m -> m.Timestamp)
                |> Seq.toList
            | _ -> []

    member this.HistoryAsync(peerUid: string) : Task<ChatMessage list> = task {
        let! raw = this.DbAsync("messages/" + this.ChatIdWith peerUid, HttpMethod.Get, None)
        return this.ParseMessages(peerUid, raw)
    }

    // --- realtime (Server-Sent Events) ---------------------------------------

    /// Сливает событие с накопленным состоянием узла:
    /// put заменяет узел целиком, patch меняет отдельные ключи.
    static member private Merge(state: JsonNode, eventName: string, data: string) : JsonNode =
        let trimmed = data.Trim()

        if trimmed = "" then
            state
        elif eventName = "put" then
            if trimmed = "null" then null else JsonNode.Parse trimmed
        elif eventName = "patch" then
            match state, JsonNode.Parse trimmed with
            | (:? JsonObject as current), (:? JsonObject as changes) ->
                for pair in changes do
                    current[pair.Key] <-
                        (if isNull pair.Value then null else pair.Value.DeepClone())

                current
            | _ -> state
        else
            state

    /// Держит поток узла открытым и отдаёт его состояние при каждом изменении.
    /// Работает до отмены ct; при обрыве и истечении токена переподключается.
    member this.WatchNodeAsync(path: string, onJson: string -> unit, ct: CancellationToken) : Task = task {
        let mutable attempt = 0
        let mutable running = true

        while running && not ct.IsCancellationRequested do
            try
                let! token = this.EnsureTokenAsync()

                let url =
                    sprintf "%s/%s.json?auth=%s" root (path.Trim '/') (Uri.EscapeDataString token)

                use request = new HttpRequestMessage(HttpMethod.Get, url)
                request.Headers.Accept.ParseAdd "text/event-stream"

                use streamClient = new HttpClient(Timeout = System.Threading.Timeout.InfiniteTimeSpan)

                use! response =
                    streamClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)

                if not response.IsSuccessStatusCode then
                    attempt <- attempt + 1
                    do! Task.Delay(TimeSpan.FromSeconds(float (min 15 (attempt * 2))), ct)
                else
                    attempt <- 0
                    use! netStream = response.Content.ReadAsStreamAsync ct
                    use reader = new StreamReader(netStream)

                    let mutable state: JsonNode = null
                    let mutable eventName = ""
                    let mutable buffer = StringBuilder()
                    let mutable alive = true

                    while alive && not ct.IsCancellationRequested do
                        let! line = reader.ReadLineAsync ct

                        if isNull line then
                            alive <- false
                        elif line.Length = 0 then
                            // Пустая строка — конец события.
                            let payload = buffer.ToString()
                            buffer.Clear() |> ignore

                            if eventName = "put" || eventName = "patch" then
                                state <- FirebaseClient.Merge(state, eventName, payload)
                                onJson(if isNull state then "null" else state.ToJsonString())
                            elif eventName = "cancel" then
                                // Токен истёк или доступ отозван — переподключимся с новым.
                                session <-
                                    session
                                    |> Option.map (fun current -> { current with ExpiresAt = DateTime.MinValue })

                                alive <- false

                            eventName <- ""
                        elif line.StartsWith "event:" then
                            eventName <- line.Substring(6).Trim()
                        elif line.StartsWith "data:" then
                            if buffer.Length > 0 then buffer.Append('\n') |> ignore
                            buffer.Append(line.Substring(5).Trim()) |> ignore

                    if not ct.IsCancellationRequested then
                        do! Task.Delay(TimeSpan.FromSeconds 1.0, ct)
            with
            | :? OperationCanceledException -> running <- false
            | _ ->
                attempt <- attempt + 1

                if not ct.IsCancellationRequested then
                    do! Task.Delay(TimeSpan.FromSeconds(float (min 15 (attempt * 2))), ct)
    }

    /// Переписка с собеседником в реальном времени.
    member this.WatchChatAsync(peerUid: string, onMessages: ChatMessage list -> unit, ct: CancellationToken) : Task =
        this.WatchNodeAsync(
            "messages/" + this.ChatIdWith peerUid,
            (fun json -> onMessages(this.ParseMessages(peerUid, json))),
            ct
        )

    /// Список пользователей в реальном времени.
    member this.WatchUsersAsync(onUsers: UserProfile list -> unit, ct: CancellationToken) : Task =
        this.WatchNodeAsync("users", (fun json -> onUsers(this.ParseUsers json)), ct)

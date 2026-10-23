namespace uqoz.Firebase

open System
open System.Globalization
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open BCrypt.Net
open uqoz.Crypto

/// Настройки подключения. URL можно переопределить переменной окружения
/// UQOZ_FIREBASE_URL (удобно для тестового проекта).
module FirebaseConfig =

    let defaultUrl = "https://uqoz-cazu-default-rtdb.europe-west1.firebasedatabase.app"

    let url () =
        match Environment.GetEnvironmentVariable "UQOZ_FIREBASE_URL" with
        | null | "" -> defaultUrl
        | value -> value

// ---------------------------------------------------------------------------
// Низкоуровневый REST-клиент Realtime Database
// ---------------------------------------------------------------------------

type Rtdb(baseUrl: string) =

    let http = new HttpClient(Timeout = TimeSpan.FromSeconds 20.0)
    let root = baseUrl.Trim().TrimEnd('/')

    member private _.Url(path: string, query: string) =
        let clean = path.Trim('/')
        let q = if String.IsNullOrEmpty query then "" else "?" + query
        sprintf "%s/%s.json%s" root clean q

    member private _.Fail(op: string, path: string, status: int, body: string) =
        raise (Exception(sprintf "Firebase %s /%s: HTTP %d — %s" op path status body))

    member this.GetAsync(path: string) : Task<string> = task {
        let! response = http.GetAsync(this.Url(path, ""))
        let! body = response.Content.ReadAsStringAsync()
        if not response.IsSuccessStatusCode then
            this.Fail("GET", path, int response.StatusCode, body)
        return body
    }

    member this.GetShallowAsync(path: string) : Task<string> = task {
        let! response = http.GetAsync(this.Url(path, "shallow=true"))
        let! body = response.Content.ReadAsStringAsync()
        if not response.IsSuccessStatusCode then
            this.Fail("GET shallow", path, int response.StatusCode, body)
        return body
    }

    member this.PutAsync(path: string, json: string) : Task<unit> = task {
        use content = new StringContent(json, Encoding.UTF8, "application/json")
        let! response = http.PutAsync(this.Url(path, ""), content)
        let! body = response.Content.ReadAsStringAsync()
        if not response.IsSuccessStatusCode then
            this.Fail("PUT", path, int response.StatusCode, body)
        return ()
    }

    /// POST создаёт узел с уникальным ключом (аналог push()).
    /// Возвращает этот ключ.
    member this.PostAsync(path: string, json: string) : Task<string> = task {
        use content = new StringContent(json, Encoding.UTF8, "application/json")
        let! response = http.PostAsync(this.Url(path, ""), content)
        let! body = response.Content.ReadAsStringAsync()
        if not response.IsSuccessStatusCode then
            this.Fail("POST", path, int response.StatusCode, body)
        use doc = JsonDocument.Parse(body)
        match doc.RootElement.TryGetProperty "name" with
        | true, name -> return name.GetString()
        | _ -> return ""
    }

    member this.DeleteAsync(path: string) : Task<unit> = task {
        let! response = http.DeleteAsync(this.Url(path, ""))
        if not response.IsSuccessStatusCode then
            let! body = response.Content.ReadAsStringAsync()
            this.Fail("DELETE", path, int response.StatusCode, body)
        return ()
    }

// ---------------------------------------------------------------------------
// Пользователи (вход по нику и паролю)
// ---------------------------------------------------------------------------

type UserProfile = {
    Name: string
    CreatedAt: DateTime
    Avatar: string
}

module Users =

    let private jsonString (node: JsonNode) =
        if isNull node then "" else node.GetValue<string>()

    let normalizeName (name: string) =
        (if isNull name then "" else name).Trim().TrimStart('@').ToLowerInvariant()

    let isValidName (name: string) =
        name.Length >= 3
        && name.Length <= 20
        && name |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_')

    let private userJson (name: string) (passwordHash: string) =
        let node = JsonObject()
        node["name"] <- JsonValue.Create(name)
        node["hash"] <- JsonValue.Create(passwordHash)
        node["createdAt"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        node["avatar"] <- JsonValue.Create("")
        node.ToJsonString()

    let registerAsync (db: Rtdb) (name: string) (password: string) : Task<Result<UserProfile, string>> = task {
        let clean = normalizeName name

        if not (isValidName clean) then
            return Result.Error "Ник: 3–20 символов, только буквы, цифры и _"
        elif String.IsNullOrWhiteSpace password || password.Length < 4 then
            return Result.Error "Пароль должен быть не короче 4 символов"
        else
            let! existing = db.GetAsync("users/" + clean)

            if not (String.IsNullOrWhiteSpace existing) && existing.Trim() <> "null" then
                return Result.Error "Этот ник уже занят"
            else
                let hash = BCrypt.HashPassword password
                do! db.PutAsync("users/" + clean, userJson clean hash)
                return Ok { Name = clean; CreatedAt = DateTime.UtcNow; Avatar = "" }
    }

    let loginAsync (db: Rtdb) (name: string) (password: string) : Task<Result<UserProfile, string>> = task {
        let clean = normalizeName name

        if String.IsNullOrWhiteSpace clean then
            return Result.Error "Введите ник"
        elif String.IsNullOrWhiteSpace password then
            return Result.Error "Введите пароль"
        else
            let! raw = db.GetAsync("users/" + clean)

            if String.IsNullOrWhiteSpace raw || raw.Trim() = "null" then
                return Result.Error "Пользователь не найден"
            else
                let node = JsonNode.Parse raw
                let hash = jsonString node["hash"]

                if String.IsNullOrWhiteSpace hash || not (BCrypt.Verify(password, hash)) then
                    return Result.Error "Неверный пароль"
                else
                    let createdAt =
                        try
                            DateTimeOffset
                                .FromUnixTimeMilliseconds(node["createdAt"].GetValue<int64>())
                                .UtcDateTime
                        with _ -> DateTime.UtcNow

                    return Ok { Name = clean; CreatedAt = createdAt; Avatar = jsonString node["avatar"] }
    }

    /// Список всех зарегистрированных ников (через shallow=true).
    let listAsync (db: Rtdb) : Task<string list> = task {
        let! raw = db.GetShallowAsync "users"

        if String.IsNullOrWhiteSpace raw || raw.Trim() = "null" then
            return []
        else
            let node = JsonNode.Parse raw

            match node with
            | :? JsonObject as obj ->
                return
                    obj
                    |> Seq.map (fun kv -> kv.Key)
                    |> Seq.sort
                    |> Seq.toList
            | _ -> return []
    }

// ---------------------------------------------------------------------------
// Чаты и сообщения
// ---------------------------------------------------------------------------

type ChatMessage = {
    Id: string
    From: string
    Text: string
    Timestamp: DateTime
}

module Chats =

    let private appSecret = "uqoz-chat-v1-7f3a9c2e"

    /// Идентификатор диалога не зависит от того, кто его открыл.
    let chatId (a: string) (b: string) =
        let x = Users.normalizeName a
        let y = Users.normalizeName b
        if String.CompareOrdinal(x, y) <= 0 then x + "__" + y else y + "__" + x

    /// Ключ шифрования, одинаковый у обоих участников конкретного чата.
    let chatKey (id: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes(id + "|" + appSecret))

    let private decode (node: JsonNode) =
        if isNull node then "" else node.GetValue<string>()

    let sendAsync (db: Rtdb) (id: string) (sender: string) (text: string) : Task<Result<unit, string>> = task {
        if String.IsNullOrWhiteSpace text then
            return Result.Error "Пустое сообщение"
        else
            let cipher, iv = CryptoService.Encrypt text (chatKey id)

            let payload = JsonObject()
            payload["from"] <- JsonValue.Create(Users.normalizeName sender)
            payload["cipher"] <- JsonValue.Create(cipher)
            payload["iv"] <- JsonValue.Create(Convert.ToBase64String iv)
            payload["ts"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())

            let! _ = db.PostAsync("messages/" + id, payload.ToJsonString())
            return Ok ()
    }

    let private parseMessages (id: string) (raw: string) =
        if String.IsNullOrWhiteSpace raw || raw.Trim() = "null" then
            []
        else
            match JsonNode.Parse raw with
            | :? JsonObject as obj ->
                obj
                |> Seq.choose (fun kv ->
                    try
                        let node = kv.Value
                        let ts =
                            try
                                DateTimeOffset
                                    .FromUnixTimeMilliseconds(node["ts"].GetValue<int64>())
                                    .LocalDateTime
                            with _ -> DateTime.MinValue

                        let cipher = decode node["cipher"]
                        let iv = decode node["iv"]

                        let text =
                            try
                                CryptoService.Decrypt cipher (chatKey id) (Convert.FromBase64String iv)
                            with _ -> "[не удалось расшифровать]"

                        Some {
                            Id = kv.Key
                            From = decode node["from"]
                            Text = text
                            Timestamp = ts
                        }
                    with _ -> None)
                |> Seq.sortBy (fun m -> m.Timestamp)
                |> Seq.toList
            | _ -> []

    let historyAsync (db: Rtdb) (id: string) : Task<ChatMessage list> = task {
        let! raw = db.GetAsync("messages/" + id)
        return parseMessages id raw
    }

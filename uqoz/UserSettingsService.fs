namespace uqoz

open System.Linq
open uqoz.Data

module UserSettingsService =

    /// Переименовать пользователя. Username — первичный ключ, поэтому
    /// меняем его через сохранение сущности, а не через Id.
    let updateUsername (currentUsername: string) (newUsername: string) : Result<unit, string> =
        let currentName = currentUsername.Trim().ToLower()
        let nextName = newUsername.Trim().ToLower()

        if System.String.IsNullOrWhiteSpace nextName || nextName.Length < 3 then
            Error "Никнейм должен быть не короче 3 символов"
        else
            use db = new AppDbContext()
            let user = db.Users.FirstOrDefault(fun u -> u.Username = currentName)

            if obj.ReferenceEquals(user, null) then
                Error "Пользователь не найден"
            else
                let isTaken = db.Users.Any(fun u -> u.Username = nextName && u.Username <> currentName)
                if isTaken then
                    Error "Этот никнейм уже занят"
                else
                    user.Username <- nextName
                    db.SaveChanges() |> ignore
                    Ok ()

    /// Обновить аватар (Base64). Пустая строка — сбросить аватар.
    let updateAvatar (username: string) (avatarBase64: string) : Result<unit, string> =
        let cleanName = username.Trim().ToLower()
        use db = new AppDbContext()
        let user = db.Users.FirstOrDefault(fun u -> u.Username = cleanName)

        if obj.ReferenceEquals(user, null) then
            Error "Пользователь не найден"
        else
            user.AvatarBase64 <- (if obj.ReferenceEquals(avatarBase64, null) then "" else avatarBase64)
            db.SaveChanges() |> ignore
            Ok ()

    /// Отметить пользователя онлайн/оффлайн.
    let setOnline (username: string) (online: bool) : Result<unit, string> =
        let cleanName = username.Trim().ToLower()
        use db = new AppDbContext()
        let user = db.Users.FirstOrDefault(fun u -> u.Username = cleanName)

        if obj.ReferenceEquals(user, null) then
            Error "Пользователь не найден"
        else
            user.IsOnline <- online
            db.SaveChanges() |> ignore
            Ok ()

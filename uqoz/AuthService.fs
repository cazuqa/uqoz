namespace uqoz.Services

open System
open System.Threading.Tasks
open Microsoft.EntityFrameworkCore
open BCrypt.Net
open uqoz.Data

type AuthResult =
    | Success of User
    | Error of string

type AuthService() =
    let mutable currentUser: User option = None

    do
        use db = new AppDbContext()
        db.Database.EnsureCreated() |> ignore

    member this.CurrentUser = currentUser

    // Регистрация по нику
    member this.RegisterAsync(username: string, password: string) : Task<AuthResult> = task {
        let cleanUsername = username.Trim().ToLower()

        if String.IsNullOrWhiteSpace(cleanUsername) || cleanUsername.Length < 3 then
            return Error "Никнейм должен быть не короче 3 символов!"
        elif String.IsNullOrWhiteSpace(password) || password.Length < 4 then
            return Error "Пароль должен быть не короче 4 символов!"
        else
            use db = new AppDbContext()
            let! existingUser = db.Users.FirstOrDefaultAsync(fun u -> u.Username = cleanUsername)

            if not (obj.ReferenceEquals(existingUser, null)) then
                return Error "Пользователь с таким ником уже существует!"
            else
                let hashedPassword = BCrypt.HashPassword(password)

                let newUser = {
                    Username = cleanUsername
                    PasswordHash = hashedPassword
                    AvatarBase64 = ""
                    CreatedAt = DateTime.UtcNow
                    IsOnline = true
                }

                db.Users.Add(newUser) |> ignore
                let! _ = db.SaveChangesAsync()

                currentUser <- Some newUser
                return Success newUser
    }

    // Вход по нику и паролю
    member this.LoginAsync(username: string, password: string) : Task<AuthResult> = task {
        let cleanUsername = username.Trim().ToLower()

        if String.IsNullOrWhiteSpace(cleanUsername) then
            return Error "Введите никнейм!"
        else
            use db = new AppDbContext()
            let! user = db.Users.FirstOrDefaultAsync(fun u -> u.Username = cleanUsername)

            if obj.ReferenceEquals(user, null) then
                return Error "Пользователь не найден!"
            else
                let isPasswordValid = BCrypt.Verify(password, user.PasswordHash)

                if not isPasswordValid then
                    return Error "Неверный пароль!"
                else
                    user.IsOnline <- true
                    let! _ = db.SaveChangesAsync()

                    currentUser <- Some user
                    return Success user
    }

    // Выход из аккаунта (ИСПРАВЛЕНО)
    member this.LogoutAsync() : Task<unit> = task {
        match currentUser with
        | Some user ->
            use db = new AppDbContext()
            let! dbUser = db.Users.FindAsync(user.Username)
            if not (obj.ReferenceEquals(dbUser, null)) then
                dbUser.IsOnline <- false
                let! _ = db.SaveChangesAsync() // Получаем асинхронный результат
                ()
            currentUser <- None
        | None -> ()
    }
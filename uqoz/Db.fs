namespace uqoz.Data

open System
open System.ComponentModel.DataAnnotations
open System.ComponentModel.DataAnnotations.Schema
open Microsoft.EntityFrameworkCore

[<CLIMutable>]
type User = {
    [<Key>]
    mutable Username: string
    mutable PasswordHash: string
    mutable AvatarBase64: string
    mutable CreatedAt: DateTime
    mutable IsOnline: bool
}

[<CLIMutable>]
type FriendRequest = {
    [<Key>]
    [<DatabaseGenerated(DatabaseGeneratedOption.Identity)>]
    mutable Id: int
    mutable SenderUsername: string
    mutable ReceiverUsername: string
    mutable Status: string // "Pending", "Accepted", "Rejected"
    mutable CreatedAt: DateTime
}

[<CLIMutable>]
type Message = {
    [<Key>]
    [<DatabaseGenerated(DatabaseGeneratedOption.Identity)>]
    mutable Id: int
    mutable SenderUsername: string
    mutable ReceiverUsername: string
    mutable EncryptedText: string // Зашифрованное через Crypto.fs сообщение
    mutable IV: string            // Вектор инициализации AES (Base64)
    mutable Timestamp: DateTime
}

type AppDbContext() =
    inherit DbContext()

    [<DefaultValue>] val mutable users: DbSet<User>
    member this.Users with get() = this.users and set v = this.users <- v

    [<DefaultValue>] val mutable friendRequests: DbSet<FriendRequest>
    member this.FriendRequests with get() = this.friendRequests and set v = this.friendRequests <- v

    [<DefaultValue>] val mutable messages: DbSet<Message>
    member this.Messages with get() = this.messages and set v = this.messages <- v

    override this.OnConfiguring(options) =
        options.UseSqlite("Data Source=uqoz.db") |> ignore
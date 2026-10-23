namespace uqoz.Services

open System
open System.Linq
open System.Threading.Tasks
open Microsoft.EntityFrameworkCore
open uqoz.Data

type FriendService() =

    // Отправить запрос в друзья по никнейму
    member this.SendRequestAsync(senderUsername: string, receiverUsername: string) : Task<Result<unit, string>> = task {
        let sender = senderUsername.Trim().ToLower()
        let receiver = receiverUsername.Trim().ToLower()

        if sender = receiver then
            return Result.Error "Нельзя добавить самого себя!"
        else
            use db = new AppDbContext()
            let! targetUser = db.Users.FirstOrDefaultAsync(fun u -> u.Username = receiver)

            if obj.ReferenceEquals(targetUser, null) then
                return Result.Error "Пользователь с таким ником не найден!"
            else
                let! existing = db.FriendRequests.FirstOrDefaultAsync(fun r -> 
                    (r.SenderUsername = sender && r.ReceiverUsername = receiver) ||
                    (r.SenderUsername = receiver && r.ReceiverUsername = sender))

                if not (obj.ReferenceEquals(existing, null)) then
                    return Result.Error "Запрос уже существует или вы уже друзья!"
                else
                    let req = {
                        Id = 0
                        SenderUsername = sender
                        ReceiverUsername = receiver
                        Status = "Pending"
                        CreatedAt = DateTime.UtcNow
                    }
                    db.FriendRequests.Add(req) |> ignore
                    let! _ = db.SaveChangesAsync()
                    return Ok ()
    }

    // Принять запрос
    member this.AcceptRequestAsync(requestId: int) : Task<unit> = task {
        use db = new AppDbContext()
        let! req = db.FriendRequests.FindAsync(requestId)
        if not (obj.ReferenceEquals(req, null)) then
            req.Status <- "Accepted"
            let! _ = db.SaveChangesAsync()
            ()
    }

    // Получить список подтверждённых друзей
    member this.GetFriendsAsync(username: string) : Task<User list> = task {
        let cleanName = username.Trim().ToLower()
        use db = new AppDbContext()

        let! acceptedReqs = 
            db.FriendRequests
                .Where(fun r -> r.Status = "Accepted" && (r.SenderUsername = cleanName || r.ReceiverUsername = cleanName))
                .ToListAsync()

        let friendUsernames = 
            acceptedReqs 
            |> Seq.map (fun r -> if r.SenderUsername = cleanName then r.ReceiverUsername else r.SenderUsername)
            |> Seq.toList

        let! friends = db.Users.Where(fun u -> friendUsernames.Contains(u.Username)).ToListAsync()
        return friends |> Seq.toList
    }
namespace uqoz.Services

open System
open System.Linq
open System.Threading.Tasks
open Microsoft.EntityFrameworkCore
open uqoz.Data
open uqoz.Crypto // Использует твой Crypto.fs

type DecryptedMessage = {
    Id: int
    SenderUsername: string
    ReceiverUsername: string
    Text: string
    Timestamp: DateTime
}

type ChatService(encryptionKey: byte[]) =

    // Отправка зашифрованного сообщения
    member this.SendMessageAsync(sender: string, receiver: string, text: string) : Task<Result<unit, string>> = task {
        if String.IsNullOrWhiteSpace(text) then
            return Result.Error "Нельзя отправить пустое сообщение!"
        else
            let cipherText, iv = CryptoService.Encrypt text encryptionKey
            
            let msg = {
                Id = 0
                SenderUsername = sender.Trim().ToLower()
                ReceiverUsername = receiver.Trim().ToLower()
                EncryptedText = cipherText
                IV = Convert.ToBase64String(iv)
                Timestamp = DateTime.UtcNow
            }

            use db = new AppDbContext()
            db.Messages.Add(msg) |> ignore
            let! _ = db.SaveChangesAsync()
            return Ok ()
    }

    // Загрузка переписки между двумя пользователями с расшифровкой
    member this.GetChatHistoryAsync(user1: string, user2: string) : Task<DecryptedMessage list> = task {
        let u1 = user1.Trim().ToLower()
        let u2 = user2.Trim().ToLower()

        use db = new AppDbContext()
        let! rawMsgs = 
            db.Messages
                .Where(fun m -> (m.SenderUsername = u1 && m.ReceiverUsername = u2) || (m.SenderUsername = u2 && m.ReceiverUsername = u1))
                .OrderBy(fun m -> m.Timestamp)
                .ToListAsync()

        let decrypted = 
            rawMsgs 
            |> Seq.map (fun m ->
                let iv = Convert.FromBase64String(m.IV)
                let plainText =
                    try
                        CryptoService.Decrypt m.EncryptedText encryptionKey iv
                    with _ -> "[Ошибка расшифровки]"
                
                {
                    Id = m.Id
                    SenderUsername = m.SenderUsername
                    ReceiverUsername = m.ReceiverUsername
                    Text = plainText
                    Timestamp = m.Timestamp
                })
            |> Seq.toList

        return decrypted
    }
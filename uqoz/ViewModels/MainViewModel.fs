namespace uqoz.ViewModels

open System
open System.Collections.ObjectModel
open CommunityToolkit.Mvvm.ComponentModel
open CommunityToolkit.Mvvm.Input
open uqoz.Security

type MessageItem(sender: string, encryptedContent: string) =
    member val Sender = sender with get, set
    member val EncryptedContent = encryptedContent with get, set
    member this.DecryptedText = Encryption.decrypt encryptedContent

type MainViewModel() =
    inherit ObservableObject()

    // Авторизация
    let mutable isLoggedIn = false
    let mutable authEmail = ""
    let mutable authPassword = ""
    let mutable authErrorText = ""

    // Чаты и сообщения
    let mutable activeChatTitle = "банда ишаков"
    let mutable newMessageText = ""
    let messages = ObservableCollection<MessageItem>()

    // Звонки
    let mutable statusText = "Готов к звонку"
    let mutable hasIncomingCall = false
    let mutable isTabMode = true

    // Инициализация дефолтных зашифрованных сообщений
    do
        messages.Add(MessageItem("ишак", Encryption.encrypt "Здарова всем в банде!"))
        messages.Add(MessageItem("брат ишака", Encryption.encrypt "Го в звонок залетайте"))

    // --- СВОЙСТВА ДЛЯ АВТОРИЗАЦИИ ---
    member this.IsLoggedIn
        with get() = isLoggedIn
        and set(v) = this.SetProperty(&isLoggedIn, v) |> ignore

    member this.AuthEmail
        with get() = authEmail
        and set(v) = this.SetProperty(&authEmail, v) |> ignore

    member this.AuthPassword
        with get() = authPassword
        and set(v) = this.SetProperty(&authPassword, v) |> ignore

    member this.AuthErrorText
        with get() = authErrorText
        and set(v) = this.SetProperty(&authErrorText, v) |> ignore

    member this.LoginCommand =
        RelayCommand(fun () ->
            if String.IsNullOrWhiteSpace(authEmail) || String.IsNullOrWhiteSpace(authPassword) then
                this.AuthErrorText <- "Заполните Email и Пароль"
            else
                this.AuthErrorText <- ""
                this.IsLoggedIn <- true // Успешный вход
        )

    // --- СВОЙСТВА И КОМАНДЫ ЧАТА ---
    member this.ActiveChatTitle
        with get() = activeChatTitle
        and set(v) = this.SetProperty(&activeChatTitle, v) |> ignore

    member this.NewMessageText
        with get() = newMessageText
        and set(v) = this.SetProperty(&newMessageText, v) |> ignore

    member this.Messages = messages

    member this.SelectChatCommand =
        RelayCommand<string>(fun chatKey ->
            match chatKey with
            | "ishak" -> this.ActiveChatTitle <- "ишак"
            | "brother" -> this.ActiveChatTitle <- "брат ишака"
            | _ -> this.ActiveChatTitle <- "банда ишаков"
        )

    member this.SendMessageCommand =
        RelayCommand(fun () ->
            if not (String.IsNullOrWhiteSpace(newMessageText)) then
                let encrypted = Encryption.encrypt newMessageText
                messages.Add(MessageItem("Я", encrypted))
                this.NewMessageText <- ""
        )

    // --- УПРАВЛЕНИЕ ЗВОНКАМИ И ПАНЕЛЬЮ ---
    member this.StatusText
        with get() = statusText
        and set(v) = this.SetProperty(&statusText, v) |> ignore

    member this.HasIncomingCall
        with get() = hasIncomingCall
        and set(v) = this.SetProperty(&hasIncomingCall, v) |> ignore

    member this.IsTabMode
        with get() = isTabMode
        and set(v) = this.SetProperty(&isTabMode, v) |> ignore

    member this.ToggleBottomPanelCommand =
        RelayCommand(fun () -> this.IsTabMode <- not this.IsTabMode)

    member this.SelectTabCommand =
        RelayCommand<string>(fun _ -> ())

    member this.StartCallCommand =
        RelayCommand(fun () ->
            this.StatusText <- "Вызов..."
            // Имитация входящего звонка для теста уведомления
            this.HasIncomingCall <- true
        )

    member this.AcceptCallCommand =
        RelayCommand(fun () ->
            this.HasIncomingCall <- false
            this.StatusText <- "Звонок активен (P2P)"
        )

    member this.RejectCallCommand =
        RelayCommand(fun () ->
            this.HasIncomingCall <- false
            this.StatusText <- "Вызов отклонен"
        )

    member this.EndCallCommand =
        RelayCommand(fun () ->
            this.HasIncomingCall <- false
            this.StatusText <- "Звонок завершен"
        )

    member this.ToggleMuteCommand = RelayCommand(fun () -> ())
    member this.ToggleScreenShareCommand = RelayCommand(fun () -> ())
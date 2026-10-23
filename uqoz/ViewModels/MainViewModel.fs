namespace uqoz.ViewModels

open System
open System.Collections.ObjectModel
open System.Threading.Tasks
open Avalonia.Threading
open CommunityToolkit.Mvvm.ComponentModel
open CommunityToolkit.Mvvm.Input
open uqoz.Firebase

/// Сообщение, уже расшифрованное для показа в UI.
type MessageItem(sender: string, text: string, timestamp: DateTime) =
    member val Sender = sender with get, set
    member val Text = text with get, set
    member val Timestamp = timestamp with get, set
    member this.TimeText = timestamp.ToString("HH:mm")

type MainViewModel() as this =
    inherit ObservableObject()

    let db = Rtdb(FirebaseConfig.url ())

    // --- авторизация ---
    let mutable isLoggedIn = false
    let mutable authUsername = ""
    let mutable authPassword = ""
    let mutable authErrorText = ""
    let mutable currentUserName = ""

    // --- чаты ---
    let contacts = ObservableCollection<string>()
    let mutable selectedContact: string = null
    let mutable activeChatTitle = "Выберите чат"
    let mutable newMessageText = ""
    let messages = ObservableCollection<MessageItem>()

    // --- звонки (пока макет) ---
    let mutable statusText = "Готов к звонку"
    let mutable hasIncomingCall = false
    let mutable isTabMode = true

    let mutable isBusy = false
    let mutable lastError = ""

    // Опрос Firebase раз в 2 секунды — простой и предсказуемый realtime
    // для десктопа (SSE добавим позже, если понадобится).
    let timer =
        new DispatcherTimer(
            TimeSpan.FromSeconds 2.0,
            DispatcherPriority.Background,
            EventHandler(fun _ _ -> this.RefreshMessagesAsync() |> ignore)
        )

    do timer.Start()

    // --- свойства авторизации ---

    member this.IsLoggedIn
        with get () = isLoggedIn
        and set value = this.SetProperty(&isLoggedIn, value) |> ignore

    member this.AuthUsername
        with get () = authUsername
        and set value = this.SetProperty(&authUsername, value) |> ignore

    member this.AuthPassword
        with get () = authPassword
        and set value = this.SetProperty(&authPassword, value) |> ignore

    member this.AuthErrorText
        with get () = authErrorText
        and set value = this.SetProperty(&authErrorText, value) |> ignore

    member this.CurrentUserName
        with get () = currentUserName
        and set value = this.SetProperty(&currentUserName, value) |> ignore

    member this.IsBusy
        with get () = isBusy
        and set value = this.SetProperty(&isBusy, value) |> ignore

    member this.LastError
        with get () = lastError
        and set value = this.SetProperty(&lastError, value) |> ignore

    // --- свойства чатов ---

    member this.Contacts = contacts

    member this.SelectedContact
        with get () = selectedContact
        and set value =
            if this.SetProperty(&selectedContact, value) then
                this.ActiveChatTitle <- (if isNull value then "Выберите чат" else value)
                messages.Clear()
                this.RefreshMessagesAsync() |> ignore

    member this.ActiveChatTitle
        with get () = activeChatTitle
        and set value = this.SetProperty(&activeChatTitle, value) |> ignore

    member this.NewMessageText
        with get () = newMessageText
        and set value = this.SetProperty(&newMessageText, value) |> ignore

    member this.Messages = messages

    // --- звонки ---

    member this.StatusText
        with get () = statusText
        and set value = this.SetProperty(&statusText, value) |> ignore

    member this.HasIncomingCall
        with get () = hasIncomingCall
        and set value = this.SetProperty(&hasIncomingCall, value) |> ignore

    member this.IsTabMode
        with get () = isTabMode
        and set value = this.SetProperty(&isTabMode, value) |> ignore

    // --- сеть ---

    member private this.CurrentChatId =
        if String.IsNullOrWhiteSpace currentUserName || String.IsNullOrWhiteSpace selectedContact then
            ""
        else
            Chats.chatId currentUserName selectedContact

    member this.RefreshMessagesAsync() : Task = task {
        let chat = this.CurrentChatId

        if this.IsLoggedIn && chat <> "" then
            try
                let! history = Chats.historyAsync db chat

                // Перерисовываем только при изменении количества, чтобы
                // не сбрасывать прокрутку на каждом тике таймера.
                if history.Length <> messages.Count then
                    messages.Clear()
                    for message in history do
                        messages.Add(MessageItem(message.From, message.Text, message.Timestamp))
            with ex ->
                this.LastError <- "Не удалось загрузить сообщения: " + ex.Message
    }

    member private this.LoadContactsAsync() : Task = task {
        try
            let! names = Users.listAsync db
            contacts.Clear()
            for name in names do
                // себя в списке собеседников не показываем
                if name <> Users.normalizeName currentUserName then
                    contacts.Add name
        with ex ->
            this.LastError <- "Не удалось получить список пользователей: " + ex.Message
    }

    member private this.AuthenticateAsync(register: bool) : Task = task {
        if not this.IsBusy then
            this.IsBusy <- true
            this.AuthErrorText <- ""
            this.LastError <- ""

            try
                let! result =
                    if register then
                        Users.registerAsync db this.AuthUsername this.AuthPassword
                    else
                        Users.loginAsync db this.AuthUsername this.AuthPassword

                match result with
                | Ok profile ->
                    this.CurrentUserName <- profile.Name
                    this.IsLoggedIn <- true
                    this.AuthPassword <- ""
                    do! this.LoadContactsAsync()

                    // Автоматически открываем первый диалог с другим ником.
                    let firstOther = contacts |> Seq.tryFind (fun n -> n <> profile.Name)
                    match firstOther with
                    | Some name -> this.SelectedContact <- name
                    | None -> this.SelectedContact <- null
                | Result.Error message -> this.AuthErrorText <- message
            with ex ->
                this.AuthErrorText <- "Нет связи с Firebase: " + ex.Message

            this.IsBusy <- false
    }

    member this.LoginCommand =
        AsyncRelayCommand(Func<Task>(fun () -> this.AuthenticateAsync false))

    member this.RegisterCommand =
        AsyncRelayCommand(Func<Task>(fun () -> this.AuthenticateAsync true))

    member this.LogoutCommand =
        RelayCommand(fun () ->
            this.IsLoggedIn <- false
            this.CurrentUserName <- ""
            this.SelectedContact <- null
            this.ActiveChatTitle <- "Выберите чат"
            this.Messages.Clear()
            this.Contacts.Clear()
            this.StatusText <- "Готов к звонку"
        )

    member this.SendMessageCommand =
        AsyncRelayCommand(
            Func<Task>(fun () ->
                task {
                    let chat = this.CurrentChatId
                    let text = this.NewMessageText

                    if chat <> "" && not (String.IsNullOrWhiteSpace text) then
                        try
                            match! Chats.sendAsync db chat this.CurrentUserName text with
                            | Ok () ->
                                this.NewMessageText <- ""
                                do! this.RefreshMessagesAsync()
                            | Result.Error message -> this.LastError <- message
                        with ex ->
                            this.LastError <- "Не удалось отправить: " + ex.Message
                })
        )

    member this.RefreshCommand =
        AsyncRelayCommand(
            Func<Task>(fun () ->
                task {
                    do! this.LoadContactsAsync()
                    do! this.RefreshMessagesAsync()
                })
        )

    // --- заглушки звонков, пока не подключён WebRTC ---

    member this.ToggleBottomPanelCommand =
        RelayCommand(fun () -> this.IsTabMode <- not this.IsTabMode)

    member this.SelectTabCommand = RelayCommand<string>(fun _ -> ())

    member this.StartCallCommand =
        RelayCommand(fun () ->
            this.StatusText <- "Вызов..."
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
            this.StatusText <- "Вызов отклонён"
        )

    member this.EndCallCommand =
        RelayCommand(fun () ->
            this.HasIncomingCall <- false
            this.StatusText <- "Звонок завершён"
        )

    member this.ToggleMuteCommand = RelayCommand(fun () -> ())
    member this.ToggleScreenShareCommand = RelayCommand(fun () -> ())

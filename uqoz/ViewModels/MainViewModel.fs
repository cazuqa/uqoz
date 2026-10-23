namespace uqoz.ViewModels

open System
open System.Collections.ObjectModel
open System.Threading
open System.Threading.Tasks
open Avalonia
open Avalonia.Styling
open Avalonia.Threading
open CommunityToolkit.Mvvm.ComponentModel
open CommunityToolkit.Mvvm.Input
open uqoz.Firebase

/// Сообщение, уже расшифрованное для показа в UI.
type MessageItem(id: string, sender: string, text: string, timestamp: DateTime, isMine: bool) =
    member val Id = id
    member val Sender = sender with get, set
    member val Text = text with get, set
    member val Timestamp = timestamp with get, set
    member val IsMine = isMine with get, set
    member this.TimeText = timestamp.ToString("HH:mm")

type MainViewModel() as this =
    inherit ObservableObject()

    let client = FirebaseClient(FirebaseConfig.apiKey(), FirebaseConfig.dbUrl())

    // --- тема ---
    let mutable themeName = "dark"

    // --- навигация в боковой панели ---
    let mutable sidebarMode = "dm" // dm | channels | account | settings

    // --- авторизация ---
    let mutable isLoggedIn = false
    let mutable authNick = ""
    let mutable authEmail = ""
    let mutable authPassword = ""
    let mutable authErrorText = ""
    let mutable currentNick = ""
    let mutable currentEmail = ""

    // --- чаты ---
    let contacts = ObservableCollection<UserProfile>()
    let mutable selectedContact: UserProfile = Unchecked.defaultof<UserProfile>
    let mutable newMessageText = ""
    let messages = ObservableCollection<MessageItem>()

    // --- потоки (realtime) ---
    let mutable chatStream: CancellationTokenSource = null
    let mutable usersStream: CancellationTokenSource = null

    // --- прочее ---
    let mutable statusText = "Готов к звонку"
    let mutable hasIncomingCall = false
    let mutable isBusy = false
    let mutable lastError = ""
    let mutable isDeleteConfirm = false

    do this.ApplyTheme themeName

    // --- тема ---

    member private this.ApplyTheme(name: string) =
        themeName <- name

        let variant =
            match name with
            | "light" -> ThemeVariant.Light
            | "system" -> ThemeVariant.Default
            | _ -> ThemeVariant.Dark

        match Application.Current with
        | null -> ()
        | app -> app.RequestedThemeVariant <- variant

        this.OnPropertyChanged "IsDarkTheme"
        this.OnPropertyChanged "IsLightTheme"
        this.OnPropertyChanged "IsSystemTheme"

    member this.IsDarkTheme = themeName = "dark"
    member this.IsLightTheme = themeName = "light"
    member this.IsSystemTheme = themeName = "system"

    member this.SetThemeCommand =
        RelayCommand<string>(fun name ->
            this.ApplyTheme(if String.IsNullOrWhiteSpace name then "dark" else name))

    // --- навигация ---

    member private this.SetSidebarMode(mode: string) =
        sidebarMode <- mode
        this.OnPropertyChanged "IsChannelsView"
        this.OnPropertyChanged "IsDmView"
        this.OnPropertyChanged "IsAccountView"
        this.OnPropertyChanged "IsSettingsView"
        this.OnPropertyChanged "IsTabMode"
        this.OnPropertyChanged "SidebarTitle"

    member this.IsChannelsView = sidebarMode = "channels"
    member this.IsDmView = sidebarMode = "dm"
    member this.IsAccountView = sidebarMode = "account"
    member this.IsSettingsView = sidebarMode = "settings"
    member this.IsTabMode = sidebarMode = "channels" || sidebarMode = "dm"

    member this.SidebarTitle =
        match sidebarMode with
        | "channels" -> "КАНАЛЫ"
        | "dm" -> "ЛИЧНЫЕ СООБЩЕНИЯ"
        | "account" -> "АККАУНТ"
        | _ -> "НАСТРОЙКИ"

    member this.ShowChannelsCommand = RelayCommand(fun () -> this.SetSidebarMode "channels")
    member this.ShowDmsCommand = RelayCommand(fun () -> this.SetSidebarMode "dm")
    member this.ShowAccountCommand = RelayCommand(fun () -> this.SetSidebarMode "account")
    member this.ShowSettingsCommand = RelayCommand(fun () -> this.SetSidebarMode "settings")

    member this.ToggleBottomPanelCommand =
        RelayCommand(fun () ->
            if this.IsTabMode then this.SetSidebarMode "account" else this.SetSidebarMode "dm")

    // --- авторизация ---

    member this.IsLoggedIn
        with get () = isLoggedIn
        and set value = this.SetProperty(&isLoggedIn, value) |> ignore

    member this.AuthNick
        with get () = authNick
        and set value = this.SetProperty(&authNick, value) |> ignore

    member this.AuthEmail
        with get () = authEmail
        and set value = this.SetProperty(&authEmail, value) |> ignore

    member this.AuthPassword
        with get () = authPassword
        and set value = this.SetProperty(&authPassword, value) |> ignore

    member this.AuthErrorText
        with get () = authErrorText
        and set value = this.SetProperty(&authErrorText, value) |> ignore

    member this.CurrentNick
        with get () = currentNick
        and set value =
            this.SetProperty(&currentNick, value) |> ignore
            this.OnPropertyChanged "CurrentInitial"

    member this.CurrentEmail
        with get () = currentEmail
        and set value = this.SetProperty(&currentEmail, value) |> ignore

    member this.CurrentInitial =
        if String.IsNullOrWhiteSpace currentNick then "?" else currentNick.Substring(0, 1).ToUpperInvariant()

    member this.IsBusy
        with get () = isBusy
        and set value = this.SetProperty(&isBusy, value) |> ignore

    member this.LastError
        with get () = lastError
        and set value = this.SetProperty(&lastError, value) |> ignore

    member this.IsDeleteConfirm
        with get () = isDeleteConfirm
        and set value = this.SetProperty(&isDeleteConfirm, value) |> ignore

    // --- контакты ---

    member this.Contacts = contacts

    member this.SelectedContact
        with get () = selectedContact
        and set value =
            if not (obj.ReferenceEquals(selectedContact, value)) then
                selectedContact <- value
                this.OnPropertyChanged "SelectedContact"
                this.OnPropertyChanged "HasContact"
                this.OnPropertyChanged "ActiveChatTitle"
                this.OnPropertyChanged "ActiveChatSubtitle"
                messages.Clear()
                this.RestartChatStream()

    member this.HasContact = not (obj.ReferenceEquals(selectedContact, null))

    member this.ActiveChatTitle =
        if obj.ReferenceEquals(selectedContact, null) then "Нет диалога" else selectedContact.Nick

    member this.ActiveChatSubtitle =
        if obj.ReferenceEquals(selectedContact, null) then
            "Выберите собеседника слева"
        else
            "Личные сообщения · AES-256 · в реальном времени"

    member this.NewMessageText
        with get () = newMessageText
        and set value = this.SetProperty(&newMessageText, value) |> ignore

    member this.Messages = messages

    // --- звонки (макет) ---

    member this.StatusText
        with get () = statusText
        and set value = this.SetProperty(&statusText, value) |> ignore

    member this.HasIncomingCall
        with get () = hasIncomingCall
        and set value = this.SetProperty(&hasIncomingCall, value) |> ignore

    // --- realtime-потоки ---

    /// Список сообщений обновляем только если он реально изменился,
    /// иначе UI дёргался бы на каждый пакет.
    member private this.ApplyMessages(list: ChatMessage list) =
        let same =
            list.Length = messages.Count
            && (list
                |> List.mapi (fun index message -> messages.[index].Id = message.Id)
                |> List.forall id)

        if not same then
            messages.Clear()

            for message in list do
                messages.Add(
                    MessageItem(
                        message.Id,
                        message.FromNick,
                        message.Text,
                        message.Timestamp,
                        message.FromUid = client.Uid
                    )
                )

    member private this.StopStreams() =
        for source in [ chatStream; usersStream ] do
            if not (isNull source) then
                try
                    source.Cancel()
                with _ -> ()

                source.Dispose()

        chatStream <- null
        usersStream <- null

    member private this.RestartChatStream() =
        if not (isNull chatStream) then
            try
                chatStream.Cancel()
            with _ -> ()

            chatStream.Dispose()
            chatStream <- null

        let peer = selectedContact

        if this.IsLoggedIn && not (obj.ReferenceEquals(peer, null)) then
            let source = new CancellationTokenSource()
            chatStream <- source
            let peerUid = peer.Uid

            client
                .WatchChatAsync(
                    peerUid,
                    (fun list ->
                        Dispatcher.UIThread.Post(fun () ->
                            if not source.IsCancellationRequested then
                                this.ApplyMessages list)),
                    source.Token
                )
                .ContinueWith(fun (finished: Task) ->
                    if finished.IsFaulted then
                        Dispatcher.UIThread.Post(fun () ->
                            this.LastError <-
                                "Поток сообщений: "
                                + finished.Exception.GetBaseException().Message))
            |> ignore

    member private this.StartUsersStream() =
        if not (isNull usersStream) then
            try
                usersStream.Cancel()
            with _ -> ()

            usersStream.Dispose()
            usersStream <- null

        if this.IsLoggedIn then
            let source = new CancellationTokenSource()
            usersStream <- source

            client
                .WatchUsersAsync(
                    (fun users ->
                        Dispatcher.UIThread.Post(fun () ->
                            if not source.IsCancellationRequested then
                                this.ApplyUsers users)),
                    source.Token
                )
                .ContinueWith(fun (finished: Task) ->
                    if finished.IsFaulted then
                        Dispatcher.UIThread.Post(fun () ->
                            this.LastError <-
                                "Список пользователей: "
                                + finished.Exception.GetBaseException().Message))
            |> ignore

    // --- работа с сетью ---

    member this.RefreshMessagesAsync() : Task = task {
        let peer = selectedContact

        if this.IsLoggedIn && not (obj.ReferenceEquals(peer, null)) then
            try
                let! history = client.HistoryAsync peer.Uid
                this.ApplyMessages history
            with ex ->
                this.LastError <- "Сообщения: " + ex.Message
    }

    member private this.ApplyUsers(users: UserProfile list) =
        let selectedUid = if obj.ReferenceEquals(selectedContact, null) then "" else selectedContact.Uid
        let peers = users |> List.filter (fun user -> user.Uid <> client.Uid)

        let unchanged =
            peers.Length = contacts.Count
            && (peers
                |> List.mapi (fun index user ->
                    contacts.[index].Uid = user.Uid && contacts.[index].Nick = user.Nick)
                |> List.forall id)

        if not unchanged then
            contacts.Clear()

            for user in peers do
                contacts.Add user

            let restored =
                if selectedUid = "" then None
                else contacts |> Seq.tryFind (fun user -> user.Uid = selectedUid)

            match restored with
            | Some user -> this.SelectedContact <- user
            | None when contacts.Count > 0 -> this.SelectedContact <- contacts.[0]
            | None -> this.SelectedContact <- Unchecked.defaultof<UserProfile>

    member private this.LoadContactsAsync() : Task = task {
        try
            let! users = client.UsersAsync()
            this.ApplyUsers users
        with ex ->
            this.LastError <- "Пользователи: " + ex.Message
    }

    member private this.AuthenticateAsync(register: bool) : Task = task {
        if not this.IsBusy then
            this.IsBusy <- true
            this.AuthErrorText <- ""
            this.LastError <- ""

            try
                let! result =
                    if register then
                        client.SignUpAsync(this.AuthNick, this.AuthEmail, this.AuthPassword)
                    else
                        client.SignInAsync(this.AuthEmail, this.AuthPassword)

                match result with
                | Ok session ->
                    this.CurrentNick <- session.Nick
                    this.CurrentEmail <- session.Email
                    this.IsLoggedIn <- true
                    this.AuthPassword <- ""
                    this.SetSidebarMode "dm"
                    do! this.LoadContactsAsync()
                    this.StartUsersStream()
                | Result.Error message -> this.AuthErrorText <- message
            with ex ->
                this.AuthErrorText <- "Ошибка: " + ex.Message

            this.IsBusy <- false
    }

    member this.LoginCommand =
        AsyncRelayCommand(Func<Task>(fun () -> this.AuthenticateAsync false))

    member this.RegisterCommand =
        AsyncRelayCommand(Func<Task>(fun () -> this.AuthenticateAsync true))

    member private this.ResetSession() =
        this.StopStreams()
        client.SignOut()
        this.IsLoggedIn <- false
        this.CurrentNick <- ""
        this.CurrentEmail <- ""
        this.SelectedContact <- Unchecked.defaultof<UserProfile>
        this.Messages.Clear()
        this.Contacts.Clear()
        this.LastError <- ""
        this.IsDeleteConfirm <- false
        this.SetSidebarMode "dm"

    member this.LogoutCommand =
        RelayCommand(fun () ->
            this.ResetSession()
            this.AuthErrorText <- ""
        )

    member this.AskDeleteAccountCommand =
        RelayCommand(fun () -> this.IsDeleteConfirm <- true)

    member this.CancelDeleteAccountCommand =
        RelayCommand(fun () -> this.IsDeleteConfirm <- false)

    member this.DeleteAccountCommand =
        AsyncRelayCommand(
            Func<Task>(fun () ->
                task {
                    if not this.IsBusy then
                        this.IsBusy <- true
                        this.LastError <- ""

                        match! client.DeleteMyAccountAsync() with
                        | Ok () ->
                            this.ResetSession()
                            this.AuthErrorText <- "Аккаунт удалён. Ник снова свободен."
                        | Result.Error message -> this.LastError <- message

                        this.IsBusy <- false
                })
        )

    member this.SendMessageCommand =
        AsyncRelayCommand(
            Func<Task>(fun () ->
                task {
                    let peer = selectedContact
                    let text = this.NewMessageText

                    if not (obj.ReferenceEquals(peer, null)) && not (String.IsNullOrWhiteSpace text) then
                        try
                            match! client.SendAsync(peer.Uid, text) with
                            | Ok () ->
                                this.NewMessageText <- ""
                                do! this.RefreshMessagesAsync()
                            | Result.Error message -> this.LastError <- message
                        with ex ->
                            this.LastError <- "Отправка: " + ex.Message
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

    // --- заглушки звонков ---

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
            this.StatusText <- "Звонок отклонён"
        )

    member this.EndCallCommand =
        RelayCommand(fun () ->
            this.HasIncomingCall <- false
            this.StatusText <- "Звонок завершён"
        )

    member this.ToggleMuteCommand = RelayCommand(fun () -> ())
    member this.ToggleScreenShareCommand = RelayCommand(fun () -> ())

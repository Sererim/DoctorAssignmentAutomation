open System
open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.Playwright

module Log =
    let info (msg: string) =
        Console.ForegroundColor <- ConsoleColor.Cyan
        printfn "[*] %s" msg
        Console.ResetColor()
    let ok (msg: string) =
        Console.ForegroundColor <- ConsoleColor.Green
        printfn "[+] %s" msg
        Console.ResetColor()
    let warn (msg: string) =
        Console.ForegroundColor <- ConsoleColor.Yellow
        printfn "[!] %s" msg
        Console.ResetColor()
    let err (msg: string) =
        Console.ForegroundColor <- ConsoleColor.Red
        printfn "[-] %s" msg
        Console.ResetColor()
    let waitUser (msg: string) =
        Console.ForegroundColor <- ConsoleColor.Magenta
        printfn ""
        printfn ">>> %s" msg
        printfn ">>> Press ENTER when ready ..."
        Console.ResetColor()
        Console.ReadLine() |> ignore

module Helpers =
    let waitVisible (locator: ILocator) (timeoutMs: float) =
        locator.First.WaitForAsync(LocatorWaitForOptions(Timeout = float32 timeoutMs, State = WaitForSelectorState.Visible))

    let tryClick (locator: ILocator) (timeoutMs: float) =
        task {
            try
                do! waitVisible locator timeoutMs
                do! locator.First.ClickAsync()
                return true
            with _ ->
                return false
        }

/// Used time slots across the whole session (e.g. "09:12")
let usedTimes = HashSet<string>(StringComparer.Ordinal)

[<EntryPoint>]
let main _argv =
    let run = task {
        Log.info "MAX Doctor Assignment Automation (Playwright / F#)"
        Log.info "Flow: web.max.ru → QR login → bot 122 → ESIA → booking loop until no slots"

        use! playwright = Playwright.CreateAsync()
        let launchOpts = BrowserTypeLaunchOptions(
            Headless = false,
            SlowMo = 40.0f,
            Args = [| "--start-maximized" |]
        )
        let! browser = playwright.Chromium.LaunchAsync(launchOpts)
        let ctxOpts = BrowserNewContextOptions(
            ViewportSize = ViewportSize(Width = 1280, Height = 900),
            Locale = "ru-RU",
            AcceptDownloads = true
        )
        let! context = browser.NewContextAsync(ctxOpts)
        let! page = context.NewPageAsync()

        // ==================================================================
        // 1. Open web.max.ru — QR login
        // ==================================================================
        Log.info "Opening https://web.max.ru ..."
        let! _ = page.GotoAsync("https://web.max.ru", PageGotoOptions(WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60000.0f))

        let qrExpired =
            page.Locator("text=QR code has expired")
                .Or(page.Locator("text=Срок действия QR-кода истёк"))
                .Or(page.Locator("h3").Filter(LocatorFilterOptions(HasText = "expired")))
                .Or(page.Locator("h3").Filter(LocatorFilterOptions(HasText = "истёк")))

        let refreshQr =
            page.Locator("button[aria-label='Refresh QR code']")
                .Or(page.Locator("button[aria-label='Обновить QR-код']"))
                .Or(page.Locator("button:has-text('Обновить')"))
                .Or(page.Locator("button:has-text('Update')"))

        Log.info "Waiting for QR screen (scan with MAX app). Auto-refresh if expired ..."
        let loginDeadline = DateTime.UtcNow.AddMinutes(6.0)
        let mutable loggedIn = false

        while not loggedIn && DateTime.UtcNow < loginDeadline do
            let searchReady = page.Locator("input[placeholder='Найти'], input[placeholder='Search']")
            let! n = searchReady.CountAsync()
            if n > 0 then
                loggedIn <- true
                Log.ok "Logged in — main MAX interface visible."
            else
                let! exp = qrExpired.CountAsync()
                if exp > 0 then
                    Log.warn "QR expired — refreshing ..."
                    let! clicked = Helpers.tryClick refreshQr 3000.0
                    if not clicked then Log.warn "Refresh button not found"
                    do! page.WaitForTimeoutAsync(1500.0f)
                do! page.WaitForTimeoutAsync(2000.0f)

        if not loggedIn then
            Log.err "Timed out waiting for QR login. Aborting."
            return 1
        else
            Log.waitUser "Confirm main chat list is visible, then press ENTER to continue."

            // ==================================================================
            // 2. Search bot «122» and open it
            // ==================================================================
            Log.info "Searching for bot «122» ..."
            let searchBox = page.Locator("input[placeholder='Найти'], input[placeholder='Search']")
            do! searchBox.First.ClickAsync()
            do! searchBox.First.FillAsync("122")
            do! page.WaitForTimeoutAsync(1800.0f)

            let botOpen =
                page.Locator("button:has-text('Open'), button:has-text('Открыть')")
            let! openCount = botOpen.CountAsync()
            if openCount = 0 then
                Log.err "Could not find Open button for the bot."
                return 1
            else
                // Prefer the one near the SPb doctor bot if multiple
                let preferred =
                    page.Locator("button:has-text('Open'), button:has-text('Открыть')")
                        .Filter(LocatorFilterOptions(HasText = "122"))
                let! prefN = preferred.CountAsync()
                if prefN > 0 then
                    do! preferred.First.ClickAsync()
                else
                    do! botOpen.First.ClickAsync()

                Log.ok "Opened bot — waiting for mini-app dialog / iframe ..."
                do! page.WaitForTimeoutAsync(2500.0f)

                let startBtn = page.Locator("button:has-text('Начать')")
                let! startN = startBtn.CountAsync()
                if startN > 0 then
                    Log.info "Clicking «Начать» ..."
                    do! startBtn.First.ClickAsync()
                    do! page.WaitForTimeoutAsync(2000.0f)

                let dialog = page.Locator("dialog.container, dialog[open]")
                try
                    do! dialog.First.WaitForAsync(LocatorWaitForOptions(Timeout = 20000.0f))
                with _ ->
                    Log.warn "Dialog wait timed out — continuing"

                let frame = page.FrameLocator("iframe[title='webapp'], iframe.iframe, .webapp iframe")
                do! page.WaitForTimeoutAsync(2000.0f)

                // ==================================================================
                // 3. «Войти через ГОСУСЛУГИ» → ESIA popup tab
                // ==================================================================
                Log.info "Looking for «Войти через ГОСУСЛУГИ» ..."
                let gosuslugiBtn =
                    frame.Locator(
                        "button:has-text('Войти через ГОСУСЛУГИ'), a:has-text('Войти через ГОСУСЛУГИ'), button:has-text('ГОСУСЛУГИ')")

                try
                    do! gosuslugiBtn.First.WaitForAsync(LocatorWaitForOptions(Timeout = 25000.0f, State = WaitForSelectorState.Visible))
                with _ ->
                    let broader = frame.Locator("button, a").Filter(LocatorFilterOptions(HasText = "ГОСУСЛУГИ"))
                    do! broader.First.WaitForAsync(LocatorWaitForOptions(Timeout = 10000.0f))

                Log.info "Clicking «Войти через ГОСУСЛУГИ» (opens ESIA tab) ..."
                let popupTask = page.WaitForPopupAsync()
                do! gosuslugiBtn.First.ClickAsync()
                let! esiaPage = popupTask
                Log.ok "ESIA tab opened."

                Log.waitUser
                    "Authenticate on Gosuslugi (password / QR / any method).\n    If ESIA QR expires you can refresh manually or wait for auto-refresh.\n    After successful login the tab redirects back to MAX.\n    Press ENTER here only if the script is stuck."

                Log.info "Waiting for ESIA authentication + redirect to MAX ..."
                let esiaDeadline = DateTime.UtcNow.AddMinutes(8.0)
                let mutable esiaDone = false
                while not esiaDone && DateTime.UtcNow < esiaDeadline do
                    try
                        let esiaQrExpired = esiaPage.Locator("text=QR-код устарел")
                        let! expN = esiaQrExpired.CountAsync()
                        if expN > 0 then
                            let refreshEsia = esiaPage.Locator("button:has-text('Обновить')")
                            let! rN = refreshEsia.CountAsync()
                            if rN > 0 then
                                Log.warn "ESIA QR expired — clicking «Обновить» ..."
                                do! refreshEsia.First.ClickAsync()
                                do! esiaPage.WaitForTimeoutAsync(1500.0f)

                        let url = esiaPage.Url.ToLowerInvariant()
                        let stillEsia =
                            url.Contains("esia") || url.Contains("gosuslugi") ||
                            url.Contains("gu-st.ru") || url.Contains("/login")
                        if not stillEsia then
                            Log.ok (sprintf "ESIA redirected (url=%s)." esiaPage.Url)
                            esiaDone <- true
                        else
                            do! Task.Delay(1200)
                    with _ ->
                        Log.ok "ESIA tab closed."
                        esiaDone <- true

                do! page.BringToFrontAsync()
                do! page.WaitForTimeoutAsync(3000.0f)

                let frame2 = page.FrameLocator("iframe[title='webapp'], iframe.iframe, .webapp iframe")
                try
                    do! dialog.First.WaitForAsync(LocatorWaitForOptions(Timeout = 20000.0f))
                    Log.ok "Back in MAX mini-app."
                with _ ->
                    Log.warn "Mini-app dialog not found after ESIA — try re-opening the bot if needed."

                // ==================================================================
                // 4. Booking loop until no free time slots remain
                // ==================================================================
                Log.info "Starting booking loop ..."
                let mutable keepGoing = true
                let mutable bookingRound = 0

                while keepGoing do
                    bookingRound <- bookingRound + 1
                    Log.info (sprintf "——— Booking round %d ———" bookingRound)

                    // 4a. «Запись на прием к врачу»
                    let bookBtn =
                        frame2.Locator("button:has-text('Запись на прием к врачу'), button:has-text('Запись на приём к врачу')")
                    let! bookOk = Helpers.tryClick bookBtn 15000.0
                    if not bookOk then
                        Log.warn "«Запись на прием к врачу» not found — stopping loop."
                        keepGoing <- false
                    else
                        do! page.WaitForTimeoutAsync(1500.0f)

                        // 4b. «Себя»
                        let sebeBtn = frame2.Locator("button:has-text('Себя')")
                        let! sebeOk = Helpers.tryClick sebeBtn 10000.0
                        if not sebeOk then
                            Log.warn "«Себя» not found — stopping."
                            keepGoing <- false
                        else
                            do! page.WaitForTimeoutAsync(1500.0f)

                            // 4c. Hospital search «10», first item, Далее
                            let hospitalSearch = frame2.Locator("input[placeholder='Поиск...'], input[type='search']")
                            try
                                do! Helpers.waitVisible hospitalSearch 10000.0
                                do! hospitalSearch.First.ClickAsync()
                                do! hospitalSearch.First.FillAsync("10")
                                do! page.WaitForTimeoutAsync(1500.0f)
                            with ex ->
                                Log.warn (sprintf "Hospital search: %s" ex.Message)

                            let hospitalRows =
                                frame2.Locator("span[class*='radiobutton'], .radiobutton__input_1")
                            let! hN = hospitalRows.CountAsync()
                            if hN > 0 then
                                do! hospitalRows.First.ClickAsync()
                                Log.ok "Selected first hospital."
                            else
                                Log.warn "No hospital radios found"

                            do! page.WaitForTimeoutAsync(800.0f)
                            let dalee1 = frame2.Locator("button:has-text('Далее')")
                            let! _ = Helpers.tryClick dalee1 8000.0
                            do! page.WaitForTimeoutAsync(1500.0f)

                            // 4d. «Терапевт»
                            let therapistRow =
                                frame2.Locator("div").Filter(LocatorFilterOptions(HasText = "Терапевт"))
                            let! tN = therapistRow.CountAsync()
                            if tN > 0 then
                                do! therapistRow.First.ClickAsync()
                                Log.ok "Selected «Терапевт»."
                            else
                                let anySpec = frame2.Locator("span[class*='radiobutton'], .radiobutton__input_1")
                                let! aN = anySpec.CountAsync()
                                if aN > 0 then do! anySpec.First.ClickAsync()

                            do! page.WaitForTimeoutAsync(600.0f)
                            let dalee2 = frame2.Locator("button:has-text('Далее')")
                            let! _ = Helpers.tryClick dalee2 8000.0
                            do! page.WaitForTimeoutAsync(1500.0f)

                            // 4e. First doctor
                            let doctorRadios =
                                frame2.Locator("span[class*='radiobutton'], .radiobutton__input_1")
                            let! dN = doctorRadios.CountAsync()
                            if dN > 0 then
                                do! doctorRadios.First.ClickAsync()
                                Log.ok "Selected first doctor."
                            else
                                Log.warn "No doctor radios found"

                            do! page.WaitForTimeoutAsync(600.0f)
                            let dalee3 = frame2.Locator("button:has-text('Далее')")
                            let! _ = Helpers.tryClick dalee3 8000.0
                            do! page.WaitForTimeoutAsync(1500.0f)

                            // 4f. Time slot not yet in usedTimes
                            let timeLocator =
                                frame2.Locator("div").Filter(
                                    LocatorFilterOptions(HasTextRegex = Text.RegularExpressions.Regex(@"^\d{1,2}:\d{2}$")))
                            let! timeCount = timeLocator.CountAsync()
                            Log.info (sprintf "Found %d time slot element(s)." timeCount)

                            let mutable chosenTime : string option = None
                            let mutable i = 0
                            while chosenTime.IsNone && i < timeCount do
                                let cell = timeLocator.Nth(i)
                                let! txt = cell.InnerTextAsync()
                                let t = txt.Trim()
                                if t.Length > 0 && not (usedTimes.Contains(t)) then
                                    try
                                        do! cell.ClickAsync()
                                        usedTimes.Add(t) |> ignore
                                        chosenTime <- Some t
                                        Log.ok (sprintf "Selected time %s (used=%d)." t usedTimes.Count)
                                    with _ -> ()
                                i <- i + 1

                            match chosenTime with
                            | None ->
                                Log.warn "No free time slots left. Stopping."
                                keepGoing <- false
                            | Some t ->
                                do! page.WaitForTimeoutAsync(800.0f)
                                let confirmTime = frame2.Locator("button:has-text('Подтвердить')")
                                let! _ = Helpers.tryClick confirmTime 8000.0
                                do! page.WaitForTimeoutAsync(1200.0f)

                                // 4g. «Записаться»
                                let zapisatsya =
                                    frame2.Locator("button:has-text('Записаться')")
                                        .Or(page.Locator("button:has-text('Записаться')"))
                                let! zOk = Helpers.tryClick zapisatsya 10000.0
                                if zOk then Log.ok (sprintf "«Записаться» for %s." t)
                                else Log.warn "«Записаться» not found"
                                do! page.WaitForTimeoutAsync(2500.0f)

                                // 4h. «На главную»
                                let naGlavnuyu =
                                    frame2.Locator("button:has-text('На главную')")
                                        .Or(page.Locator("button:has-text('На главную')"))
                                let! homeOk = Helpers.tryClick naGlavnuyu 12000.0
                                if homeOk then Log.ok "«На главную» — next round."
                                else Log.warn "«На главную» not found"
                                do! page.WaitForTimeoutAsync(2000.0f)

                Log.ok (sprintf "Finished. Booked %d slot(s). Times: %s"
                    usedTimes.Count
                    (String.Join(", ", usedTimes)))
                Log.waitUser "Browser stays open. Press ENTER to exit."
                do! Task.Delay(-1)
                return 0
    }

    run.GetAwaiter().GetResult()

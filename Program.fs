open System
open System.Runtime.InteropServices
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

[<EntryPoint>]
let main (argv : string array) : int =
    let t = task {
        Log.info "Starting MAX doctor-assignment automation (Playwright / F#)"
        Log.info "Target: https://web.max.ru  →  bot «122 Запись к врачу»  →  ESIA login"

        use! playwright = Playwright.CreateAsync()
        let browserOpts = BrowserTypeLaunchOptions(
            Headless = false,
            SlowMo = 50.0f,
            Args = [| "--start-maximized" |]
        )
        let! browser = playwright.Chromium.LaunchAsync(browserOpts)
        let contextOpts = BrowserNewContextOptions(
            ViewportSize = ViewportSize(Width = 860, Height = 640),
            Locale = "ru-RU",
            AcceptDownloads = false
        )
        let! context = browser.NewContextAsync(contextOpts)
        let! page = context.NewPageAsync()

        // ------------------------------------------------------------------
        // 1. Open web.max.ru and handle QR-code login
        // ------------------------------------------------------------------
        Log.info "Navigating to https://web.max.ru ..."
        let! _ = page.GotoAsync("https://web.max.ru", PageGotoOptions(WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60000.0f))

        let qrActiveSelector  = "h3:has-text('Sign in to MAX via QR code'), h3:has-text('Войдите в MAX по QR-коду')"
        let qrExpiredSelector = "h3:has-text('QR code has expired'), h3:has-text('Срок действия QR-кода истёк')"
        let refreshQrSelector = "button[aria-label='Refresh QR code'], button[aria-label='Обновить QR-код']"

        Log.info "Waiting for QR-code screen ..."
        let! _ = page.WaitForSelectorAsync($"{qrActiveSelector}, {qrExpiredSelector}", PageWaitForSelectorOptions(Timeout = 30000.0f))

        let mutable loggedIn = false
        let loginDeadline = DateTime.UtcNow.AddMinutes(5.0)

        while (not loggedIn) && DateTime.UtcNow < loginDeadline do
            let searchInput = page.Locator("input[placeholder='Найти']")
            let! count = searchInput.CountAsync()
            if count > 0 then
                loggedIn <- true
                Log.ok "Login detected – main interface is ready."
            else
                let expired = page.Locator(qrExpiredSelector)
                let! expCount = expired.CountAsync()
                if expCount > 0 then
                    Log.warn "QR code expired – clicking refresh ..."
                    let refreshBtn = page.Locator(refreshQrSelector)
                    let! refCount = refreshBtn.CountAsync()
                    if refCount > 0 then
                        do! refreshBtn.First.ClickAsync()
                        do! page.WaitForTimeoutAsync(1500.0f)
                    else
                        Log.warn "Refresh button not found, will retry ..."
                do! page.WaitForTimeoutAsync(2000.0f)

        if not loggedIn then
            Log.err "Timed out waiting for QR scan / login. Aborting."
        else
            Log.waitUser "You are logged in. The script will now search for the bot.\n    Make sure the main chat list is visible."

            // ------------------------------------------------------------------
            // 2. Search for the bot «122 Запись к врачу»
            // ------------------------------------------------------------------
            Log.info "Opening search and typing «122 Запись к врачу» ..."
            let searchBox = page.Locator("input[placeholder='Найти']")
            do! searchBox.ClickAsync()
            do! searchBox.FillAsync("122 Запись к врачу")
            do! page.WaitForTimeoutAsync(1500.0f)

            let resultsList = page.Locator(".searchResultsList, [class*='searchResults']")
            do! resultsList.First.WaitForAsync(LocatorWaitForOptions(Timeout = 10000.0f))

            Log.info "Looking for the bot result and its «Открыть» button ..."
            let botItem =
                page.Locator("button.item, div.item, [class*='cell']")
                    .Filter(LocatorFilterOptions(HasText = "122"))
                    .Filter(LocatorFilterOptions(HasText = "Запись"))
            let openBtn = botItem.Locator("button:has-text('Открыть'), button.button--primary:has-text('Открыть')")

            let! openCount = openBtn.CountAsync()
            let mutable opened = false
            if openCount = 0 then
                let anyOpen = page.Locator("button:has-text('Открыть')")
                let! anyCount = anyOpen.CountAsync()
                if anyCount > 0 then
                    do! anyOpen.First.ClickAsync()
                    opened <- true
                else
                    Log.err "Could not find «Открыть» button for the bot. Aborting."
            else
                do! openBtn.First.ClickAsync()
                opened <- true

            if opened then
                Log.ok "Clicked «Открыть» – waiting for the mini-app dialog ..."

                // ------------------------------------------------------------------
                // 3. Wait for the Svelte dialog that hosts the Gorzdrav mini-app
                // ------------------------------------------------------------------
                let dialog = page.Locator("dialog.container, dialog[open]")
                do! dialog.First.WaitForAsync(LocatorWaitForOptions(Timeout = 15000.0f))
                Log.ok "Mini-app dialog appeared."

                let frameLocator = page.FrameLocator("iframe[title='webapp'], iframe.iframe, .webapp iframe")
                do! page.WaitForTimeoutAsync(3000.0f)

                Log.info "Looking for «Войти через ГОСУСЛУГИ» button inside the mini-app iframe ..."
                let gosuslugiBtn =
                    frameLocator.Locator(
                        "button:has-text('Войти через ГОСУСЛУГИ'), a:has-text('Войти через ГОСУСЛУГИ'), button:has-text('ГОСУСЛУГИ'), [class*='gosuslugi']")

                try
                    do! gosuslugiBtn.First.WaitForAsync(LocatorWaitForOptions(Timeout = 20000.0f, State = WaitForSelectorState.Visible))
                with _ ->
                    Log.warn "Primary selectors missed – trying broader search ..."
                    let broader = frameLocator.Locator("button, a").Filter(LocatorFilterOptions(HasText = "ГОСУСЛУГИ"))
                    do! broader.First.WaitForAsync(LocatorWaitForOptions(Timeout = 10000.0f))

                Log.info "Clicking «Войти через ГОСУСЛУГИ» (will open ESIA login tab) ..."
                let popupTask = page.WaitForPopupAsync()
                do! gosuslugiBtn.First.ClickAsync()
                let! esiaPage = popupTask

                Log.ok "ESIA / Gosuslugi login tab opened."
                Log.waitUser "Please complete authentication on the Gosuslugi page.\n    After successful login the tab will close automatically.\n    The script is waiting for that tab to disappear ..."

                // ------------------------------------------------------------------
                // 4. Wait until the ESIA tab is closed
                // ------------------------------------------------------------------
                try
                    let deadline = DateTime.UtcNow.AddMinutes(5.0)
                    let mutable closed = false
                    while (not closed) && DateTime.UtcNow < deadline do
                        try
                            let! _ = esiaPage.TitleAsync()
                            do! Task.Delay(2000)
                        with _ ->
                            closed <- true
                    if closed then
                        Log.ok "ESIA tab closed – authentication finished."
                    else
                        Log.warn "ESIA tab did not close within 5 minutes – continuing anyway."
                with ex ->
                    Log.warn (sprintf "While waiting for ESIA tab: %s" ex.Message)

                Log.ok "First part of the flow completed successfully."
                Log.info "You can now continue with the next steps of the doctor-assignment process."
                Log.waitUser "Press ENTER to keep the browser open for inspection, or close the window to exit."

                // Keep the process alive so the browser stays open
                do! Task.Delay(-1)

        return 0
    }

    t.GetAwaiter().GetResult()

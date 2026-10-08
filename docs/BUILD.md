# Сборка для Windows

Сборка выполняется на Windows x64 с Git, PowerShell и .NET Framework 4.x.
Дополнительный SDK, Python и права администратора не требуются.

```powershell
git clone https://github.com/tigramaan/ru-direct-proxy.git
cd ru-direct-proxy
.\fetch-dependencies.ps1
.\build-package.ps1
.\test-package.ps1
```

При необходимости разрешите выполнение этих сценариев только для текущего
сеанса PowerShell в соответствии с политикой своего компьютера.

`fetch-dependencies.ps1` скачивает официальный sing-box, зафиксированную
версию российской базы GeoIP и тексты лицензий. Адреса и SHA-256 указаны
в `dependencies.json`; хеши проверяются до использования файлов.

`build-package.ps1` также запускает получение зависимостей, если они отсутствуют.
Он проверяет имеющиеся файлы, компилирует `CountryProxy.cs` штатным компилятором
из `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`, затем создаёт:

```text
build/ru-direct-proxy-v0.1.0-windows-x64.zip
build/ru-direct-proxy-v0.1.0-windows-x64.zip.sha256.txt
```

В ZIP включается только известный список файлов. Каталоги `work/`, `vendor/`,
журналы и сохранённые настройки в репозиторий и пользовательский архив не входят.
В ZIP входят проверенные бинарные зависимости из `vendor/`.

Не используйте в качестве основы комплекта установленную рабочую папку
`%LOCALAPPDATA%\BrowserCountryProxy`: она содержит настройки конкретного пользователя.

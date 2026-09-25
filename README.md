# File Command Center

Et lille dashboard til at samle adgangen til dine filer (Excel, PDF, billeder, dokumenter, mapper, links – alle typer) ét sted, uanset om de ligger lokalt, på et netværksdrev eller i OneDrive. Dashboardet gemmer kun **stien** til hver fil; filerne flyttes eller kopieres aldrig.

En rigtig Windows-app (.NET 8 + WebView2), uden nogen webserver eller åben port. Se **[desktop/README.md](desktop/README.md)** for byg-instruktioner, hvad appen gør på pc'en, og krav.

## Funktioner

- Filvælger, "Scan mapper…" og en indbygget mappebrowser til at tilføje filer og mapper.
- Hubs, tags, favoritter, arkiv og filstatus (findes/mangler filen).
- Forhåndsvisningsrude (som i Stifinder) med regnearks-/CSV-tabel, billeder, PDF'er og tekst/kode-filer – bredden kan trækkes.
- Åbner filer i det program, Windows har knyttet til dem, og blokerer altid programmer/scripts (`.exe`, `.ps1`, `.js` osv.).
- Daglige backups af dine data, plus en ekstra backup hver gang en gemning ville fjerne filer.
- Appen tjekker selv for nye versioner (GitHub Releases) og kan installere en opdatering med ét klik.
- Dansk/engelsk: sprogknappen nederst i sidebaren skifter hele grænsefladen og manualen. Standard er dansk; vælges automatisk ud fra styresystemets sprog, før dine indstillinger er indlæst.

## Data

Dine data (`data.json` + `backups/`) gemmes lokalt i `%LOCALAPPDATA%\FileCommandCenter\` og er **ikke** en del af dette repo.

## Hent appen

Fra [Releases](https://github.com/BahneGork/file-command-center/releases/latest) - to former, samme app:
- **Installer (.msi)** - installerer i `Program Files`, kræver admin, giver en Start-menu-genvej og selv-opdatering. Byg-instruktioner: **[desktop/installer/README.md](desktop/installer/README.md)**.
- **Portabel (.zip)** - pak ud og kør `FileCommandCenter.exe` hvor som helst, ingen installation, ingen admin-rettigheder nødvendigt. `web`-mappen skal blive liggende ved siden af .exe'en. Opdateres ved selv at hente en ny .zip igen - den indbyggede "Opdater nu" er lavet til installer-udgaven og vil installere en separat kopi i `Program Files`, ikke opdatere den portable kopi i sig selv.

Begge er selvstændige builds (intet .NET skal være installeret i forvejen).

## Ikke gjort endnu

- **Kodesignering.** Installeren er usigneret, så Windows vil advare (SmartScreen/Defender). IT skal godkende eller signere den, før den bredt kan installeres på arbejdscomputere.
- Se `desktop/README.md` og `desktop/installer/README.md` for flere detaljer om, hvad der mangler.

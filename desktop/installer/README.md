# Installer

Bygger en .msi til File Command Center med [WiX Toolset](https://wixtoolset.org/) v5 (**ikke** v6/v7 - de kræver
accept af en betalt "Open Source Maintenance Fee"-EULA, som vi har undgået).

## Hvad den gør

Installerer i `Program Files\File Command Center\` (kræver admin) og opretter en Start-menu-genvej. Ingen
skrivebordsgenvej, ingen registrering af filtyper. `data.json` ligger fortsat i `%LOCALAPPDATA%\FileCommandCenter\`
og påvirkes ikke af installation/afinstallation.

## Byg

```bash
dotnet tool install --global wix --version 5.0.2
wix extension add WixToolset.UI.wixext/5.0.2 -g
```

Byg altid **fra publish-outputtet, med en rigtig Windows-sti som working directory** - WiX's binder bruger Windows
Installer-databaseAPI'et direkte, og det fejler med "The Windows Installer service failed to start" (MSI 1631), hvis
man kører fra en netværks-/UNC-sti (fx `\\wsl.localhost\...`, som er hvad denne mappe ligger på fra WSL). Kopiér
`Package.wxs` og `..\publish\` til en lokal `C:\`-mappe, og byg derfra:

```powershell
dotnet publish ..\CommandCenter.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -o C:\build\publish

Copy-Item Package.wxs C:\build\
cd C:\build
wix build Package.wxs -arch x64 -ext WixToolset.UI.wixext -d PublishDir=publish -out FileCommandCenter.msi
```

`msiserver`-tjenesten (Windows Installer) skal køre - den er normalt sat til "Manual" og starter typisk af sig selv,
men start den manuelt (`Start-Service msiserver`), hvis bygningen fejler med MSI 1631.

## Ny version / udgivelse (release)

Appen tjekker automatisk **GitHub Releases** på `BahneGork/file-command-center` for en nyere version (se
`../UpdateCheck.cs`) og lader brugeren installere den direkte fra en bjælke i appen. Det kræver, at hver udgivelse
har en `.msi` vedhæftet som "asset" - der er ingen CI her, så det gøres manuelt:

1. **`UpgradeCode` i `Package.wxs` (`dc319aa0-1725-4cc3-8c86-3dbd4fb3a308`) må aldrig ændres** - det er det, der
   lader en nyere version opdatere en ældre i stedet for at installere ved siden af den.
2. Sæt **samme** nye versionsnummer to steder: `<Version>` i `../CommandCenter.csproj` og `Version=` i `Package.wxs`.
3. Byg publish-outputtet og MSI'en som beskrevet ovenfor.
4. Byg den portable udgave for sig, med `-p:Flavor=portable` lagt til `dotnet publish`-kommandoen og sin egen
   output-mappe (fx `-o C:\build\portable`), og zip den mappes indhold direkte som
   `FileCommandCenter-<version>-portable-win-x64.zip`. Den portable exe må aldrig komme i MSI'en, og omvendt -
   det er mærket i exe'en, der afgør, om appen opdaterer sig selv med MSI'en eller .zip'en.
5. Tag og udgiv med begge filer vedhæftet (tag skal starte med `v`, fx `v1.0.2` - det er det, appen sammenligner sin
   egen version imod):
   ```
   gh release create v1.0.2 FileCommandCenter.msi FileCommandCenter-1.0.2-portable-win-x64.zip --title "v1.0.2" --notes "Hvad der er ændret"
   ```
6. Kør git-commit/push af kildekoden (inkl. de to versionstal) separat - releasen er ikke bundet til et bestemt
   commit på nogen særlig måde ud over tagget.

Kun brugere, der allerede kører en version, som selv har opdateringstjekket i sig (denne eller nyere), vil se
opdateringsbjælken. En bruger på en ældre, manuelt kopieret build skal opdatere manuelt én sidste gang.

## Ikke gjort endnu

- **Signering.** MSI'en er usigneret, så Windows SmartScreen/Defender vil advare. Se overvejelserne i
  `../README.md`.
- Ingen skrivebordsgenvej eller "Reparér/Afinstallér"-genvej i Start-menuen ud over standard Windows-håndtering
  (Indstillinger → Apps).

# Installer WinBoost (FR)

## Installer
1. Télécharge `WinBoostSetup.exe` sur la [page Releases](https://github.com/PixelStack1/WinBoost/releases). Il faut Windows 10 (2004+) ou Windows 11, 64 bits.
2. Double-clique dessus. Windows SmartScreen peut afficher « Windows a protégé votre ordinateur » : clique sur **Informations complémentaires** puis **Exécuter quand même** (l'appli n'est pas signée).
3. Accepte la demande d'administrateur (WinBoost modifie des réglages système, il en a besoin).
4. Choisis ta langue, laisse le raccourci Bureau coché si tu le veux, et termine. WinBoost s'installe dans `C:\Program Files\WinBoost` et peut se lancer tout de suite.

## Premier lancement
1. Clique sur **Actualiser** : WinBoost lit l'état actuel de ton PC.
2. Va dans **Profils** et choisis **Recommandé** (aucun risque) ou **Compétitif**, ou utilise **Booster mon PC** sur le tableau de bord.
3. Un point de restauration est créé avant chaque modification. L'interrupteur reste activé.
4. Clique sur **Appliquer**. Certains réglages (HAGS, timer…) demandent un **redémarrage**.

## Revenir en arrière
- Désactive l'interrupteur d'un réglage puis **Appliquer**, ou clique sur **Tout annuler**.
- En dernier recours : *Panneau de configuration → Récupération → Ouvrir la Restauration du système*.

## Désinstaller
*Paramètres → Applications → WinBoost → Désinstaller*. Pense à cliquer sur **Tout annuler** avant si tu veux retrouver tous tes réglages d'origine. Le désinstalleur ferme WinBoost s'il est ouvert, supprime les tâches de réapplication auto, puis te demande s'il faut effacer tes paramètres, journaux et sauvegardes (`%LocalAppData%\WinBoost`) — réponds Non pour les garder.

## Compiler soi-même
Prérequis : Windows 10 2004+ / 11 x64, [SDK .NET 8](https://dotnet.microsoft.com/download/dotnet/8.0) (x64).
```powershell
cd C:\chemin\vers\winboost
powershell -ExecutionPolicy Bypass -File .\build.ps1
```
Résultat : `Bureau\WinBoost\WinBoost.exe` (seul fichier visible ; ~440 fichiers du runtime à côté marqués cachés — WinUI 3 ne peut pas être un exe unique) + un raccourci sur le Bureau. Installateur optionnel : `ISCC.exe WinBoost.iss` (Inno Setup 6).

# sign-licence — phase-1 licensing tool

Runs on the owner's PC only. `dotnet run --project tools/LicenceSigner -- <command>`, or build
once (`dotnet build tools/LicenceSigner -c Release`) and use `sign-licence.exe` from
`tools/LicenceSigner/bin/Release/net6.0/`.

1. **Once:** `sign-licence keygen <a folder outside any repo>\catalogbridge-licence.key`
   → paste the printed public key into `License.PublicKeys` in the app. Back the `.key` file
   up offline. It is the only thing that can issue licences; it is never committed
   (`*.key` is git-ignored).
2. **Per shop:** the shop opens Ρυθμίσεις → «Άδεια χρήσης» and reads you the
   *Αποτύπωμα υπολογιστή* (`9F2C-A1B3-…`). Then:

   ```
   sign-licence sign --key <the .key> --customer "ΣΤΟΑ ΑΕ" --fingerprint 9F2C-... --modules catalogue,orders --until 2027-09-22 --store 1046
   ```

   Send the printed token to the shop; they paste it in the same screen → «Ενεργοποίηση».
3. **Renewal / new PC / more modules:** sign a new token (pass the same `--licence` key to
   keep the id); the shop pastes it over the old one.

`--modules catalogue` alone = Συγχρονισμός Καταλόγου only; `orders` = Αντιστοίχιση
Παραγγελιών & Πελατών (Πελάτες, till orders → history, cancel). `orders` without
`catalogue` is allowed but pointless. `--store` binds the token to one PointToOrder store id
(informational in phase 1; enforced in phase 2).

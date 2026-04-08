# Microsoft Store

VaultSync has a reserved Microsoft Store identity and an initial packaging scaffold under `packaging/VaultSync.Store`.

Current reserved identity:

- `Identity Name`: `FlavioGiacchetti.480851279F98B`
- `Publisher`: `CN=D0FF8AE9-15EE-487F-B2F3-0913EFDA0CED`
- `Publisher Display Name`: `Flavio Giacchetti`
- `Package Family Name`: `FlavioGiacchetti.480851279F98B_e8epvg776k60t`
- `Store ID`: `9N9HRX4JCLCP`

Channel model:

- `Direct`
  - GitHub installer and updater stay enabled
- `Store`
  - Microsoft Store package and Store-managed updates
  - GitHub self-update must stay disabled

Before submission, the following still need to be completed:

- runtime distribution-channel awareness
- Store-specific update UI and support messaging
- packaged-app validation for local folders, removable drives, restore targets, and UNC/NAS paths
- Partner Center submission assets and compliance checklist

---
trigger: always_on
description: Project Localization & Translation Rule (en_US.json and pt_BR.json)
---

# Regra de Tradução / Localization Rule

1. Toda string visível na interface gráfica do **GitManager** deve estar definida nos arquivos JSON de tradução:
   - `src/GitManager/Locales/en_US.json` (Inglês - idioma padrão ao abrir o aplicativo pela primeira vez)
   - `src/GitManager/Locales/pt_BR.json` (Português do Brasil)
2. Sempre que qualquer texto, botão, rótulo, mensagem, diálogo ou tooltip for adicionado ou modificado no projeto, **ambos** os arquivos (`en_US.json` e `pt_BR.json`) devem ser obrigatoriamente atualizados juntos na mesma alteração.
3. Na primeira execução do aplicativo (quando `config.json` é criado), o idioma padrão (`Language`) deve iniciar sempre como `"en_US"`.

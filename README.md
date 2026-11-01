# 📦 GitManager

Aplicação Windows moderna, leve e portátil em WPF (.NET 8) para criar e manter atualizada automaticamente uma biblioteca de repositórios do GitHub salvos exclusivamente em pacotes **`.zip`** versionados.

## ✨ Principais Recursos

- **100% Baseado em `.zip` (`GitManagerLibrary`)**:
  - Cada repositório adicionado possui sua própria pasta dentro de `GitManagerLibrary/`.
  - Nenhum repositório precisa ser clonado ou aberto: cada nova versão detectada no GitHub é baixada como um pacote `.zip` com data, tag e hash do commit, preservando todas as versões anteriores.
- **Grade Compacta (5 colunas) + Categorias Coloridas**:
  - Cards ocupando 20% da largura horizontal com tag de categoria destacada no topo.
  - Clique na tag de categoria para alterar ou criar novas categorias personalizadas.
- **Modal de Detalhes e Notas de Update**:
  - Clique no nome do repositório para abrir um modal organizado com a descrição completa, seletor de versões `.zip` e todas as notas de Release e últimos commits vindos do repositório original.
- **Filtros e Ordenação Rápida**:
  - Filtre por Categoria, busque por texto e ordene por **Recentes**, **Estrelas**, **Nome (A-Z / Z-A)** ou **Tamanho (Maior / Menor)**.
- **🚀 Criar Projeto sem Alterar o Original**:
  - Escolha qualquer versão `.zip` salva no card e extraia um novo projeto limpo na pasta desejada, mantendo o arquivo `.zip` original intacto na biblioteca.
- **Segundo Plano na Bandeja e Auto-Registro no Windows**:
  - Ao abrir o `GitManager.exe` uma vez, ele se auto-registra na inicialização do usuário no Windows e permanece ativo na bandeja do sistema (perto do relógio).
- **Auto-Atualização Integrada**:
  - Conectado diretamente às Releases oficiais deste repositório (`Hakkemet/git_manager`).

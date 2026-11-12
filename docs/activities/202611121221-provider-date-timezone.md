# Regra de datas brasileiras dos provedores

Pedido do mantenedor em 12/11/2026: documentar, inclusive inline, que Efí e Asaas
usam GMT−3/Brasília quando o fuso não vier especificado. Respeitar qualquer fuso
explícito, manter vencimentos civis sem deslocamento e não substituir datas
bancárias pelo relógio de processamento.

Escopo concluído: README e comentários nos leitores; nenhuma alteração de lógica,
dados ou publicação. Verificação do conteúdo executável antes/depois, removendo
apenas linhas vazias e comentários de linha: idêntico em todos os arquivos tocados.
`git diff --check` e referências locais conferidos. Testes de runtime não executados:
esta entrega é somente documental.

Estado encontrado e registrado: O leitor atual já usa Brasília para valores sem offset, mas consulta a zona
`America/Sao_Paulo`, que pode aplicar horário de verão histórico. Esse caso ainda
precisa de alinhamento com o padrão fixo −03:00.

A documentação cobre os dois gateways. As pendências de parser acima continuam
explícitas e não equivalem a correções implementadas. Commit/push na main após
verificação; nenhuma worktree criada.

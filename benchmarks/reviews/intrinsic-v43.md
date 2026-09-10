# Intrinsic v43 — revisão da implementação

Data: 2026-09-08. Modelo observado: `gpt-5.4-mini`. Mantidas duas chamadas paralelas, sem retrieval e sem revisor adicional.

## Alterações

- O extractor preserva o espaçamento original dos nós de texto inline. Separadores são adicionados para blocos e `br`, não após cada nó de texto.
- Evidências recebem `blockId` derivado da posição do bloco no outline imutável. A exclusão de escrita em filler compara blocos, não citações inteiras. Somente candidatos a placeholder encontrados no outline e com marcador válido participam dessa exclusão.
- `writingErrors` passa a ser uma lista de ocorrências com cinco campos: `quote`, `problemSpan`, `errorType`, `explanation`, `correctedQuote`. O prompt pede julgamento no contexto da sentença e justificação da alteração, não uma alternativa de estilo.
- A agregação preserva erros diferentes na mesma citação, usando citação e trecho problemático. O motivo agregado inclui as explicações das ocorrências aceitas, não apenas a primeira.
- O benchmark armazena evidências aceitas/rejeitadas e aceita expectativas de correção por caso. Endesa exige as duas correções especificadas e nenhuma adicional.

## Verificações

- Build sem erros ou avisos; `git diff --check` sem erros de whitespace.
- 14 verificações locais sem LLM passaram: pontuação inline, separação por `br` e blocos, título com tag inline, identidade de blocos, schema e reextração de três HTMLs salvos.
- Benchmark principal: **16/16**, uma rodada. Relatório: `../../benchmarks/results/benchmark-v43-contextual-evidence-full.json`.
- Endesa: `decision → decisión` e `compañia → compañía`, sem correções dentro do filler.
- Cinco reanálises adicionais usaram HTMLs salvos da Cylogy, reextraídos com o código novo, preservando URL e metadata originais; não foram novas capturas da página viva.

| Snapshot Cylogy | Resultado observado |
| --- | --- |
| Content migration | Healthy; desapareceu o falso espaçamento em `include,`. |
| Company | Duas ocorrências: ausência de `of` em `a superior level quality`; vírgula entre sujeito e verbo em `redesign, features`. Desapareceu o falso espaçamento em `with you.`. |
| Optimizely development | Duas ocorrências: `for you users` e `Oprimizely`. Desapareceu o falso espaçamento em `platform?`. |
| Sitecore technology | Três ocorrências; permanece a correção questionável `lend → lends`, apoiada na interpretação do sujeito composto como singular. |
| Symposium 2020 recap | Sete ocorrências; entre erros legítimos, permanecem `team attend → team attends` e `stuck out → stood out`, que não demonstram erro objetivo. |

## Limite do resultado

O resultado de 16/16 mede aprovação do benchmark principal, não precisão universal. As reanálises adicionais não foram contadas como testes integralmente aprovados. O refactor resolve problemas determinísticos de extração e preservação das evidências, mas não demonstrou resolver todos os falsos positivos linguísticos. Não foram introduzidas exceções de vocabulário nem novas chamadas para esconder esses casos.

Os cinco JSONs completos das reanálises estão em `C:/Users/alvar/AppData/Local/Temp/versa-v43-check/bin/Debug/net10.0/`, nomeados com o identificador do snapshot original. Esse diretório é temporário.

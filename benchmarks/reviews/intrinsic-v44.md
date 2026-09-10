# Intrinsic v44 — correções localizadas e revisão

Versão final: `intrinsic-v44-reviewed-agreement`.

## Implementação

- Wrappers são percorridos na ordem dos nós: runs inline são emitidos uma vez; blocos estruturais interrompem o run. O caso Written By mantém autor e label juntos.
- Cada ocorrência tem `correctionStatus`: `safe` exige correção literal localizada; `requires_review` exige `correctedQuote: null`. Se a existência do erro for incerta, a ocorrência deve ser omitida.
- Concordância exige revisão humana mesmo se o verificador aprovar uma substituição. A decisão conservadora foi tomada após observar aprovação indevida de `lend → lends`. O contrato impede publicar essa substituição como sugestão quando a ocorrência é classificada como `agreement`.
- O código deriva `edit` (`start`, `length`, `replacement`) da substituição de um `problemSpan` único na citação. Índices são unidades UTF-16 dentro de `quote`, não coordenadas na página. Nada fora desse intervalo pode mudar. Correções compostas devem ficar para revisão ou usar um trecho contextual que abranja o reparo inseparável.
- Depois das duas chamadas paralelas, uma terceira chamada condicional verifica todos os candidatos de escrita em lote. Pode rejeitar um candidato ou retirar sua correção; não pode criar candidatos nem novas substituições. A resposta precisa cobrir todos os índices exatamente uma vez. Falha ou contrato incompleto deixa a auditoria inconclusiva, preservando outros problemas demonstrados.
- A etapa adicional foi introduzida após dois testes em que o prompt continuou marcando concordância coletiva britânica como inválida. Não há allowlist lexical em produção. O modelo é o mesmo configurado para a auditoria.
- `auditScope` documenta as exclusões existentes do extractor. Não há promessa de auditar todos os pixels visíveis. O papel estrutural continua em `blockRole`; recorrência não é inferida a partir de uma única página.
- `benchmarks/summarize-writing-findings.ps1` agrupa por host, contexto da frase e trecho. Mantém URLs, blocos, correções e ocorrências. `observed_across_pages` não comprova identidade de componente compartilhado e não muda o veredito da página.
- O benchmark compara individualmente os campos declarados, consome uma ocorrência por expectativa, rejeita duplicatas e suporta `forbiddenWritingSpans` contextualizado por citação e trecho. As duas correções Endesa agora especificam também span, tipo e estado.
- Replay local: `--pipeline intrinsic --snapshot <result.json> [outputLanguage]` reutiliza HTML e metadados originais e reconstrói o outline. Não faz captura/retrieval externo; continua chamando o modelo para a análise.

## Verificação reproduzível

```powershell
.\benchmarks\run-local-checks.ps1
.\benchmarks\run-benchmark.ps1 -AdditionalCasesPath benchmarks\writing-controls.json -ResultsPath benchmarks/results/benchmark-v44-final-reviewed.json
.\benchmarks\run-benchmark.ps1 -CasesPath benchmarks\cylogy-frozen.json -IncludeLive -ResultsPath benchmarks/results/benchmark-cylogy-v44-frozen-reviewed.json
.\benchmarks\run-benchmark.ps1 -CasesPath benchmarks\cylogy-live.json -IncludeLive -ResultsPath benchmarks/results/benchmark-cylogy-v44-live-reviewed.json
.\benchmarks\summarize-writing-findings.ps1 -ResultsPath benchmarks/results/benchmark-cylogy-v44-live-reviewed.json -OutputPath benchmarks/results/benchmark-cylogy-v44-grouped.json
```

Os cinco snapshots congelados vêm das capturas v43 de 2026-09-08. Seus hashes constam em `benchmarks/frozen/manifest.json`. Os HTMLs anteriores do benchmark principal continuam nos locais originais.

## Resultados intermediários preservados

- `benchmarks/results/benchmark-v44-full.json`: 16/16 originais; controles negativos expuseram concordância coletiva. O primeiro positivo com “helps you users” não era um controle inequívoco e foi substituído por repetição de preposição. A alteração do fixture não é apresentada como melhora do modelo.
- `benchmarks/results/benchmark-v44-controls-r2.json`: concordância coletiva continuou falhando apesar do prompt explícito; repetição e typo reais passaram.
- `benchmarks/results/benchmark-v44-controls-verified.json`: 3/3 após a verificação adicional.
- Verificações locais: 22 de extração/contrato/schema/verificação + 7 do comparador do benchmark.

## Resultado final — 2026-09-08

- Build final do projeto: 0 erros e 0 warnings.
- 32 verificações locais: 22 de extração/contrato/verificador, 7 do comparador, 3 de recorrência.
- Benchmark principal + controles: **16/19**. São 13/16 casos originais e 3/3 controles novos. Arquivo: `benchmarks/results/benchmark-v44-final-reviewed.json`. As expectativas não foram relaxadas.
- Falhas: `Persistently unprofessional writing` emitiu `brand_safety_risk` adicional; `Directly contradictory guidance` retornou duas polaridades `affirmed` para `opposite_polarity` e foi rejeitado pelo contrato; Endesa omitiu `compañia → compañía`. Rodadas anteriores também mostraram omissão de `decision → decisión`. Os prompts semânticos não foram alterados nesta implementação.
- Cinco capturas v43 reanalisadas com a versão final, sem falhas técnicas: `benchmarks/results/benchmark-cylogy-v44-frozen-reviewed.json`. Content Migration ficou healthy; Sitecore deixou o reparo de concordância para revisão. Symposium ficou healthy nesta reanálise, apesar de erros conhecidos no texto: houve omissões da inspeção e rejeições pelo verificador. Isso é uma limitação de cobertura, não uma aprovação editorial manual.
- 30 páginas vivas, versão final: **14 healthy / 16 unhealthy**, 32 evidências de escrita em 23 citações distintas, 26 sugestões marcadas safe e 6 ocorrências para revisão; 1 title/body mismatch; **0 falhas técnicas e 78 chamadas**. Arquivos: `benchmarks/results/benchmark-cylogy-v44-live-reviewed.json` e `benchmarks/results/benchmark-cylogy-v44-summary.json`. Os três shards finais foram consolidados verificando 30 URLs distintas e a mesma versão.
- Recorrência: o contexto de Spare the Air foi encontrado em 8 páginas; o contexto de Salesforce em 5. O relatório mantém todos os spans e ocorrências, inclusive quando os modelos escolheram limites diferentes para a mesma região. `benchmarks/results/benchmark-cylogy-v44-grouped.json` possui 24 assinaturas de evidência, não uma contagem manualmente estabelecida de defeitos únicos.

## Revisão dos achados e limites

- Na rodada viva final, os controles conhecidos `team attend`, `stuck out` e `thru` não geraram correções, e a lista do controle sem Oxford comma foi aceita. Não existe regra lexical de produção escondendo essas palavras.
- Correções úteis preservadas incluem `Oprimizely`, `developmer`, `Exerience`, `in in`, `is helps`, `you users` e a vírgula entre sujeito e verbo em `redesign, features`.
- `delivers … lend` foi classificado como `agreement` e saiu sem substituição, com `requires_review`. Essa política é conservadora para toda a categoria, não uma exceção para Sitecore.
- `are more → and more` foi aceito na execução viva final, mas havia sido perdido em uma revisão intermediária. A auditoria continua sensível à resposta do modelo.
- A frase `Here’s five…` foi aceita pelo verificador como uso informal em uma reanálise e reportada como erro em outra. Continua sendo uma decisão linguística instável.
- `the long-term efficacy and a passion` permanece como finding para revisão e merece revisão humana também quanto à existência do erro; retirar a correção automática não prova que o diagnóstico é válido.
- A passagem Salesforce recebeu spans de tamanhos diferentes e uma sugestão com mais de uma alteração dentro do span. A validação garante localização e preservação do exterior do span; não demonstra, sozinha, que há um único erro linguístico.
- A redução de 59 para 32 evidências em relação ao relatório v43 não é uma medida de precisão: mistura abstenção, omissões, mudanças de segmentação, decisões do modelo e possíveis diferenças da captura viva. Não há gold set completo dessas 30 páginas.

**A implementação está validada nos controles determinísticos, mas a pipeline ainda não está totalmente aprovada pelo benchmark nem validada para correção automática irrestrita.** As três falhas do benchmark e a instabilidade de cobertura ficam abertas e explícitas para o próximo ciclo. A terceira chamada melhora controles específicos, porém seu ganho global de precisão ainda precisa ser medido com anotações humanas.


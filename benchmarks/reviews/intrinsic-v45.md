# Intrinsic v45 — chamada exclusiva de writing_errors

Data: 2026-09-08. Versão: `intrinsic-v45-dedicated-writing`. Modelo mantido: `gpt-5.4-mini`.

## Mudança implementada

Três chamadas iniciadas antes de `Task.WhenAll`, sobre o mesmo snapshot imutável:

1. `page_understanding`: entendimento semântico; prompt e schema preservados.
2. `editorial_quality_inspection`: somente `unprofessionalWriting` e `publishedPlaceholders`.
3. `writing_errors_inspection`: somente `writingErrors`, com contexto completo dos blocos e contrato próprio.

Foram removidos a chamada sequencial `writing_verification`, seu schema, prompt e código de aplicação dos veredictos. Nenhuma chamada revisa a saída de outra. Não houve troca de modelo nem introdução de exceções lexicais.

Os resultados são reunidos antes da validação: placeholders identificados pela chamada editorial continuam excluindo evidências de escrita no mesmo bloco. Permanecem evidência literal, deduplicação por citação/span, edições localizadas e a política conservadora de revisão para ocorrências classificadas como `agreement`. Se qualquer inspeção falhar, a auditoria fica incompleta; outras evidências demonstradas são preservadas. O custo normal é de três chamadas por página, inclusive páginas sem candidatos de escrita.

## Verificação local

Build sem erros ou warnings. **33 verificações locais passaram**: 23 de extração/contratos/conversão/política, 7 do comparador de evidências e 3 de recorrência. Os testes verificam que a chamada editorial não pode produzir writing_errors e que a chamada de escrita não produz placeholders. O guard de concordância funciona sem depender do verificador removido.

```powershell
.\benchmarks\run-local-checks.ps1
.\benchmarks\run-benchmark.ps1 -AdditionalCasesPath benchmarks\writing-controls.json -ResultsPath benchmarks/results/benchmark-v45-dedicated-writing-full.json
.\benchmarks\run-benchmark.ps1 -CasesPath benchmarks\cylogy-frozen.json -IncludeLive -ResultsPath benchmarks/results/benchmark-cylogy-v45-frozen.json
.\benchmarks\run-benchmark.ps1 -CasesPath benchmarks\frozen-writing-controls.json -ResultsPath benchmarks/results/benchmark-v45-writing-repeat-1.json
.\benchmarks\run-benchmark.ps1 -CasesPath benchmarks\frozen-writing-controls.json -ResultsPath benchmarks/results/benchmark-v45-writing-repeat-2.json
```

Os controles repetidos reutilizam capturas existentes da v44, com URL, HTML e metadados preservados. Não são novos exemplos criados para favorecer a configuração. Os prompts da v45 não foram alterados entre as execuções relatadas.

## Resultado

| Execução | Resultado |
|---|---|
| Principal + controles | **14/19**; relatório `benchmarks/results/benchmark-v45-dedicated-writing-full.json` |
| Repetição dos quatro controles 1 | **3/4**; relatório `benchmarks/results/benchmark-v45-writing-repeat-1.json` |
| Repetição dos quatro controles 2 | **3/4**; relatório `benchmarks/results/benchmark-v45-writing-repeat-2.json` |
| Cinco capturas antigas Cylogy | 5 concluídas, sem falha técnica; `benchmarks/results/benchmark-cylogy-v45-frozen.json` |

Foram verificadas **32 auditorias / 96 chamadas**, todas com exatamente as três etapas esperadas concluídas, sem a etapa de verificação antiga. Resumo: `benchmarks/results/benchmark-v45-validation-summary.json`.

- **Endesa passou 3/3**: `decision → decisión` e `compañia → compañía`, sem alterações no bloco Lorem ipsum. Na rodada final v44 o caso falhava por omissão de uma correção.
- **Controles positivos e autor inline passaram 3/3**, cada um.
- **Controle regional falhou 3/3**: `team attend` continua sendo interpretado como erro. No controle isolado é marcado `agreement`, portanto não gera substituição, mas ainda gera um finding falso. Na captura do Symposium apareceu como `grammar` e recebeu sugestão de substituição: a política baseada em errorType depende da classificação correta pelo modelo.
- O benchmark completo ficou abaixo dos **16/19 da rodada final v44**. Isso não demonstra que toda diferença foi causada pela separação: também houve variação nas avaliações semânticas cujo prompt não mudou.

## Cinco falhas no benchmark principal

1. **Unverified claims are outside intrinsic scope**: a chamada de escrita tentou corrigir labels concatenados e chegou a usar uma afirmação histórica sobre GDPR como erro gramatical. Essa última ocorrência é uma violação do escopo solicitado ao modelo; não houve consulta externa para produzi-la.
2. **Persistently unprofessional writing**: `brand_safety_risk` adicional na chamada semântica.
3. **Abusive and coercive brand language**: ausência do `brand_safety_risk` esperado.
4. **Bufete Padilla Altea Norwegian route**: finding de escrita adicional, com `Bestill Time → Bestill Konsultasjon`, além do language_mismatch esperado.
5. **regional-controls**: falso erro de concordância coletiva.

## Leitura das capturas Cylogy

A escrita exclusiva produziu 20 evidências aceitas nas cinco capturas, contra 9 na reanálise final v44 dos mesmos arquivos de entrada. Esse aumento não equivale a precisão nem recall medidos, pois não existe anotação completa dessas capturas.

- Optimizely preservou `you users` e `Oprimizely`, acrescentando ocorrências de pontuação.
- Company preservou `a superior level quality` e `redesign,`, mas voltou a sugerir `90’s → 90s`.
- Symposium recuperou achados que tinham sido omitidos ou rejeitados na v44, mas também reportou `team attend` e uma alteração de nome próprio.
- Content Migration passou a unhealthy por novos achados gramaticais/pontuação. Não retornaram os espaços artificiais do extractor; os novos julgamentos precisam de revisão, em particular pontuação opcional.
- Sitecore manteve `delivers` e `lend` para revisão, mas os reportou como duas ocorrências. Surgiu também um finding questionável sobre `our enterprise organizations`.

## Conclusão desta alteração

A arquitetura solicitada está implementada e a cobertura do caso Endesa melhorou nas três execuções. **A chamada exclusiva, sozinha, ainda não resolveu o julgamento linguístico nem demonstrou melhora geral de precisão.** A v45 fica registrada com seus resultados reais, sem reintroduzir o verificador, alterar expectativas ou esconder falhas. As 30 páginas vivas não foram reexecutadas nesta rodada; a comparação Cylogy utilizou os cinco snapshots já disponíveis.

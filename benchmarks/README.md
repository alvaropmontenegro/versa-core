# Benchmarks do versa-core

Este diretório concentra os casos, capturas, resultados, logs, relatórios e scripts dos benchmarks.

| Local | Conteúdo |
|---|---|
| `*.json` nesta pasta | Definições de casos e entradas de experimentos |
| `results/` | Resultados das execuções, resumos e comparações históricas |
| `results/parallel/` | Saídas das execuções paralelas antigas |
| `logs/` | Logs de execução históricos |
| `snapshots/` | Páginas HTML de teste |
| `frozen/`, `frozen-v46/`, `frozen-writing-controls/` | Capturas preservadas para replay |
| `reviews/` | Diagnósticos e relatórios por versão |
| `v46-model-responses/`, `v47-model-responses/` | Respostas originais dos modelos |
| `*.ps1`, `*.cs.txt` | Executores, comparadores e verificações locais |
| `file-moves.json` | Registro dos arquivos reorganizados e hashes anteriores à atualização de referências |

Execute a partir de `versa-core`:

```powershell
# Verificações locais, sem chamadas ao modelo
./benchmarks/run-local-checks.ps1

# Benchmark principal; resultado padrão em benchmarks/results/
./benchmarks/run-benchmark.ps1

# Controles adicionais
./benchmarks/run-benchmark.ps1 -AdditionalCasesPath benchmarks/writing-controls.json -ResultsPath benchmark-custom.json

# Replay de capturas existentes
./benchmarks/run-benchmark.ps1 -CasesPath benchmarks/v46-frozen-full.json -ResultsPath benchmark-replay.json

# Resumo dos achados
./benchmarks/summarize-writing-findings.ps1 -ResultsPath benchmarks/results/benchmark-replay.json -OutputPath writing-summary.json
```

Um nome simples em `ResultsPath` ou `OutputPath` é salvo em `benchmarks/results/`. Caminhos explícitos com diretório continuam sendo respeitados. `captureArtifact` nos manifestos continua relativo à raiz de `versa-core`.

As páginas artificiais servidas pelo projeto web permanecem em `versa-web/test-site`, pois fazem parte dessa aplicação. As saídas normais da CLI em `bin/.../output` também não foram alteradas. Esta reorganização não modifica prompts, lógica de auditoria ou expectativas dos benchmarks.

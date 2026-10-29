Projeto de estudo

Melhorias iniciais nesta branch:
- thresholds de reconhecimento separados para perfil, escopo, postura e componentes
- normalizacao opcional das imagens antes do template matching
- suporte a analise offline via `Mat` no `PatternMatcher`
- modo de diagnostico por config com ranking de candidatos e salvamento opcional de crops

Ferramentas e recursos adicionados:
- `PrecisionMotion.Benchmark benchmark [--dataset <dir>|--synthetic]`: acurácia por classe, top-1/top-3, matriz de confusão (CSV) e erros com score alto em `benchmark_output/offline`. Dataset: `<dir>/<profile|optic|stance>/<Classe>/*.png`
- `PrecisionMotion.Benchmark calibrate --resolution <LxA>`: valida arquivos, thresholds, áreas (tamanho, limites da tela, sobreposição), templates ausentes e auto-reconhecimento dos templates
- histerese temporal (`histerese_confirmacoes`, `histerese_margem_saida`, `histerese_margem_ambiguidade` no `config.json`) aplicada a perfil, escopo, componentes e postura
- cadência de leitura dos anexos (`cadencia_anexos`) separada da leitura de perfil e postura
- telemetria persistida (`telemetria`, `telemetria_diretorio`): histórico curto, score médio por classe, "sem match" e crops de falha
- painel "Diagnóstico de Reconhecimento" na tela principal
- `ConfigProvider.LoadIssues`: problemas de carregamento de config deixam de ser silenciosos

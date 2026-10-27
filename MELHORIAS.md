# Melhorias Mapeadas

Este documento registra as melhorias identificadas para evoluir a base atual do projeto.

## Prioridades

### 2. Histerese e confianca temporal
- Exigir `N` leituras consecutivas antes de trocar arma, postura ou acessorio.
- Separar limiar de entrada e limiar de saida para evitar oscilacao.
- Introduzir janela curta de estabilidade para reduzir alternancia quando dois templates ficam com score parecido.
- Registrar o ultimo score aceito para apoiar diagnostico.

### 3. Painel de diagnostico na UI
- Exibir melhor candidato atual e score.
- Exibir top `N` candidatos por tipo de reconhecimento.
- Exibir threshold ativo por categoria.
- Exibir status de normalizacao do reconhecimento.
- Exibir caminho do diretorio de diagnostico e quantidade de crops salvos.

### 4. Runner de calibracao por resolucao
- Validar se a resolucao ativa possui todos os arquivos esperados.
- Verificar se areas configuradas possuem tamanho valido.
- Detectar templates ausentes para armas, escopos, componentes e postura.
- Gerar relatorio de inconsistencias por resolucao.
- Permitir execucao offline para inspecao sem depender do fluxo principal.

### 5. Refino de performance
- Reaproveitar buffers e `Mat` quando possivel.
- Diminuir recriacao de objetos em loops de reconhecimento.
- Separar cadencias de leitura por categoria de reconhecimento.
- Evitar recaptura desnecessaria quando a mesma regiao ja estiver em cache.
- Medir tempo medio por etapa para localizar gargalos.

## Melhorias neutras de engenharia que podem ser feitas com seguranca
- Benchmark offline com relatorio por classe e score.
- Validacao mais forte de configuracao no carregamento.
- Telemetria e logs estruturados.
- Mais testes automatizados para matcher, configuracao e diagnostico.
- Melhor organizacao do dominio de reconhecimento em servicos dedicados.

## Status
- Documento criado.
- Implementacao dos itens 2, 3, 4 e 5 nao foi iniciada neste passo.

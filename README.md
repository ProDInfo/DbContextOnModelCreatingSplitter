# DbContext OnModelCreating Splitter
Herramienta de línea de comandos para extraer las definiciones FluentAPI dentro de DbContext.OnModelCreating() y generar archivos de configuración (IEntityTypeConfiguration) separados.

Útil como solución temporal para: https://github.com/aspnet/EntityFrameworkCore/issues/8434

Resumen de comportamientos clave
- Los ficheros de configuración se generan en el subdirectorio de configuraciones (--configs-subdir).
- Los ficheros de entities se escriben siempre en la carpeta de salida de entities (--entities-subdir).
- Renombrado de entities (añadir sufijo con --entity-suffix):
  - Si no se especifica --output-dir, O si la carpeta destino de entities coincide con la carpeta de entrada de entities, se renombra (y se sobrescribe) el fichero de entrada original.
  - Si se especifica --output-dir y la carpeta de entities de salida es distinta de la de entrada, el origen se deja intacto y sólo se generan/renombran los ficheros en la salida.
- Por defecto se crea una copia de seguridad del DbContext original salvo que se pase -B/--no-backup.

Opciones y uso
```
  -c, --context-file   (requerido) Ruta al fichero DbContext de entrada
      --input-dir      Directorio de entrada para los ficheros entity/model (por defecto: directorio del DbContext)
  -o, --output-dir     Directorio base de salida (si se omite, la salida se considera el directorio de contexto)
      --context-subdir Subruta dentro de output-dir donde escribir el DbContext (por defecto: .)
      --entities-subdir Subruta dentro de output-dir donde escribir los entities (por defecto: Entities)
      --configs-subdir  Subruta dentro de output-dir donde escribir las configuraciones (por defecto: Configurations)
  -n, --namespace      Namespace base que se usa para generar namespaces en subcarpetas
  -s, --config-suffix  Sufijo para las clases de configuración (por defecto: Configuration)
  -t, --entity-suffix  Sufijo para las clases entity (ej.: "Entity") — activa el renombrado cuando procede
  -B, --no-backup      No guardar copia de seguridad del DbContext original
  -r, --no-replace     No sustituir las llamadas modelBuilder.Entity(...) por ApplyConfiguration(...) en el DbContext
  -e, --embed-configs  Insertar la clase de configuración generada dentro del fichero de entidad (embed)
  --help               Mostrar ayuda
  --version            Mostrar versión
```

Ejemplos
1) Generar en un directorio de salida distinto (no se toca la fuente original):
```
DbContextOnModelCreatingSplitter -c Input\Data\MyContext.cs --input-dir Input\Entities -o Output --context-subdir Contexts --entities-subdir Entities --configs-subdir Configurations
```
Resultado:
- Se generan: Output\Contexts\MyContext.cs (DbContext actualizado), Output\Entities\... (entities generados), Output\Configurations\... (configs generados)
- Los archivos originales en Input\Entities permanecen sin cambios.

2) Ejecutar sin --output-dir (salida en el mismo directorio que la entrada):
```
DbContextOnModelCreatingSplitter -c Input\Data\MyContext.cs --input-dir Input\Entities -t Entity -s Configuration
```
Resultado:
- Se genera MyContext.cs actualizado en el mismo directorio.
- Los ficheros de entidad en Input\Entities son renombrados si se ha especificado --entity-suffix (por ejemplo TaskBlueprint -> TaskBlueprintEntity) y su contenido actualizado.
- Se crea backup del DbContext original salvo que se haya usado -B.

Notas de implementación y mantenimiento
- Si se usan `--entity-suffix` y los nombres ya incluyen el sufijo, no se renombra.
- `--embed-configs` posibilita mantener la clase de configuración dentro del fichero de entity en lugar de generar un fichero separado.
- Si `--no-replace` está activo, el código dentro de OnModelCreating se deja intacto y únicamente se generan los archivos de configuración.

Descarga
Puedes descargar binarios desde las releases: https://github.com/ProDInfo/DbContextOnModelCreatingSplitter/releases

Licencia
MIT

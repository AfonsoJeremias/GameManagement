import errno
import os
import re
import shutil
import sys
import zipfile
from datetime import datetime
from encodings import utf_8
from pathlib import Path

import yaml

configuration = 'Debug'
target = 'net462'

# APENAS GameManagement
PLUGIN = 'GameManagement'

version_regex = re.compile(r'\d.\d.\d')
package_search_string = '<PackageReference Include="PlayniteSDK" Version="'


def validate_path(path: Path, is_dir=True):
    if not path.exists():
        raise FileNotFoundError(errno.ENOENT, os.strerror(errno.ENOENT), path)
    if is_dir and not path.is_dir():
        raise NotADirectoryError(errno.ENOTDIR, os.strerror(errno.ENOTDIR), path)


def decode_utf8(b) -> str:
    return utf_8.decode(b)[0]


def get_build_output_path(plugin_path: Path) -> Path:
    result = plugin_path.joinpath('bin', configuration, target)
    validate_path(result)
    return result


def validate_version(version) -> str:
    regex_match = version_regex.match(version)
    if regex_match is None:
        raise ValueError(f'Invalid Version: {version}')
    return version


def parse_new_version() -> str:
    new_version = sys.argv[2]
    return validate_version(new_version)


def get_playnite_version(csproj_path: Path) -> str:
    validate_path(csproj_path, is_dir=False)

    package_version = None
    with csproj_path.open('r', encoding='utf-8') as file:
        for line in file:
            index = line.find(package_search_string)
            if index == -1:
                continue
            package_version = (line[index+len(package_search_string):])[:5]
            break

    if package_version is None:
        raise ValueError(f'Unable to find package reference in {csproj_path}')

    return validate_version(package_version)


def copy_plugin(build_output_path: Path, output_path: Path):
    plugin_output_path = output_path.joinpath(PLUGIN)

    if plugin_output_path.exists():
        shutil.rmtree(plugin_output_path)

    print(f'Copying build output files from {build_output_path} to {plugin_output_path}')
    shutil.copytree(build_output_path, plugin_output_path)


def pack_plugin(build_output_path: Path, output_path: Path):
    zip_output_path = output_path.joinpath(f'{PLUGIN}.pext')
    print(f'Packing plugin {PLUGIN} to {zip_output_path}')
    with zipfile.ZipFile(zip_output_path, 'w', zipfile.ZIP_DEFLATED) as myzip:
        for root, dirs, files in os.walk(build_output_path):
            for file in files:
                myzip.write(os.path.join(root, file), file)


def update_plugin_manifest(src_path: Path, new_version: str):
    extension_file = src_path.joinpath(PLUGIN, 'extension.yaml')
    validate_path(extension_file, False)

    print(f'Updating manifest of plugin {PLUGIN} at {extension_file}')

    with extension_file.open('r', encoding='utf-8') as file:
        extension_manifest = yaml.safe_load(file)

    extension_manifest['Version'] = new_version

    with extension_file.open('w', encoding='utf-8') as file:
        yaml.safe_dump(extension_manifest, file)


def update_installer_manifest(manifests_dir: Path, new_version: str, playnite_version: str):
    manifest_path = manifests_dir.joinpath(f'{PLUGIN}.yaml')
    validate_path(manifest_path, is_dir=False)

    print(f'Updating installer manifest of plugin {PLUGIN} at {manifest_path}')

    with manifest_path.open('r', encoding='utf-8') as file:
        installer_manifest = yaml.safe_load(file)

    packages = installer_manifest.get('Packages', [])
    existing_package = next((p for p in packages if p['Version'] == new_version), None)
    if existing_package is not None:
        print(f'Package already exists for {PLUGIN} v{new_version}')
        return

    release_date = datetime.now()

    new_package = {
        'Version': new_version,
        'RequiredApiVersion': playnite_version,
        'ReleaseDate': release_date.strftime('%Y-%m-%d'),
        'PackageUrl': f'https://github.com/erri120/Playnite.Extensions/releases/download/v{new_version}/{PLUGIN}.pext'
    }

    packages.insert(0, new_package)
    installer_manifest['Packages'] = packages

    with manifest_path.open('w', encoding='utf-8') as file:
        yaml.safe_dump(installer_manifest, file)


def main():
    if len(sys.argv) != 3:
        raise ValueError('Not enough arguments!')

    src_path = Path(os.path.abspath('src'))
    validate_path(src_path)

    manifests_dir = Path(os.path.abspath('manifests'))
    validate_path(manifests_dir)

    mode = sys.argv[1]

    csproj_path = src_path.joinpath('Extensions.Common', 'Extensions.Common.csproj')
    validate_path(csproj_path, is_dir=False)

    if mode == 'update':
        playnite_version = get_playnite_version(csproj_path)
        new_version = parse_new_version()
        update_plugin_manifest(src_path, new_version)
        update_installer_manifest(manifests_dir, new_version, playnite_version)
    elif mode == 'pack':
        output_path = Path(os.path.abspath(sys.argv[2]))
        validate_path(output_path)

        plugin_path = src_path.joinpath(PLUGIN)
        validate_path(plugin_path)
        build_output_path = get_build_output_path(plugin_path)

        pack_plugin(build_output_path, output_path)
    elif mode == 'copy':
        output_path = Path(os.path.abspath(sys.argv[2]))
        validate_path(output_path)

        plugin_path = src_path.joinpath(PLUGIN)
        validate_path(plugin_path)
        build_output_path = get_build_output_path(plugin_path)

        copy_plugin(build_output_path, output_path)
    else:
        raise ValueError(f'Unknown mode: {mode}')


if __name__ == '__main__':
    main()

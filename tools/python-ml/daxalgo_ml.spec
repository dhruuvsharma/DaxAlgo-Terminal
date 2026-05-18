# -*- mode: python ; coding: utf-8 -*-
# PyInstaller spec for the daxalgo-ml sidecar. Build with:
#   pip install pyinstaller
#   pyinstaller daxalgo_ml.spec
# The output exe lands in tools/python-ml/dist/daxalgo-ml.exe and is what the WPF app
# spawns when AiAnalystOptions.Enabled flips on. No Python venv required on the user's box.

block_cipher = None


a = Analysis(
    ['daxalgo_ml/app.py'],
    pathex=['.'],
    binaries=[],
    datas=[],
    hiddenimports=[
        'uvicorn.logging',
        'uvicorn.protocols',
        'uvicorn.protocols.http',
        'uvicorn.protocols.http.auto',
        'uvicorn.protocols.websockets',
        'uvicorn.protocols.websockets.auto',
        'uvicorn.lifespan',
        'uvicorn.lifespan.on',
        'mplfinance',
        'matplotlib.backends.backend_agg',
        'langchain_openai',
        'langchain_anthropic',
        'langgraph',
    ],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
    noarchive=False,
)

pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.zipfiles,
    a.datas,
    [],
    name='daxalgo-ml',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=True,
    disable_windowed_traceback=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)

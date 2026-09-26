"""
Stardew Valley iOS Mod Manager — Vortex Edition
Features:
- Direct Apple USB AFC connection to iPhone/iPad
- Native Vortex library integration (sync mods from Vortex to iOS)
- Nexus Mods API integration & direct NXM one-click protocol downloads
- Mod toggle/disable, save backups, live SMAPI log stream
"""

import os
import sys
import json
import socket
import asyncio
import threading
from typing import Optional

from PyQt6.QtWidgets import (
    QApplication, QMainWindow, QWidget, QVBoxLayout, QHBoxLayout,
    QLabel, QPushButton, QTabWidget, QTableWidget, QTableWidgetItem,
    QHeaderView, QFileDialog, QMessageBox, QLineEdit, QTextEdit,
    QProgressBar, QFrame, QSplitter, QCheckBox, QAbstractItemView
)
from PyQt6.QtCore import Qt, QThread, pyqtSignal, QTimer
from PyQt6.QtGui import QColor, QFont, QIcon, QDragEnterEvent, QDropEvent
from PyQt6.QtNetwork import QTcpServer, QHostAddress

try:
    from tools.mod_manager.backend import IOSModBackend, ModInfo
    from tools.mod_manager.nexus import (
        NexusAPI, load_config, save_config, register_nxm_protocol,
        is_nxm_registered_to_us, get_vortex_stardew_dirs, DEFAULT_DOWNLOAD_DIR
    )
except ImportError:
    from backend import IOSModBackend, ModInfo
    from nexus import (
        NexusAPI, load_config, save_config, register_nxm_protocol,
        is_nxm_registered_to_us, get_vortex_stardew_dirs, DEFAULT_DOWNLOAD_DIR
    )


IPC_PORT = 49182

DARK_STYLE = """
QMainWindow {
    background-color: #191B21;
}
QWidget {
    background-color: #191B21;
    color: #E2E4E9;
    font-family: 'Segoe UI', Tahoma, Arial, sans-serif;
    font-size: 13px;
}
QFrame#HeaderBar {
    background-color: #21242D;
    border-bottom: 1px solid #313543;
    padding: 10px 16px;
}
QTabWidget::pane {
    border: none;
    background-color: #191B21;
}
QTabBar::tab {
    background-color: #21242D;
    color: #9A9EAB;
    padding: 10px 20px;
    margin-right: 4px;
    border-top-left-radius: 6px;
    border-top-right-radius: 6px;
    font-weight: bold;
    font-size: 13px;
}
QTabBar::tab:selected {
    background-color: #2A2E3B;
    color: #DA7C21;
    border-bottom: 3px solid #DA7C21;
}
QTabBar::tab:hover:!selected {
    background-color: #262935;
    color: #FFFFFF;
}
QPushButton {
    background-color: #DA7C21;
    color: #FFFFFF;
    border: none;
    border-radius: 5px;
    padding: 8px 16px;
    font-weight: bold;
    font-size: 13px;
}
QPushButton:hover {
    background-color: #E88E35;
}
QPushButton:pressed {
    background-color: #C66A17;
}
QPushButton#SecondaryBtn {
    background-color: #313543;
    color: #E2E4E9;
}
QPushButton#SecondaryBtn:hover {
    background-color: #3D4253;
}
QPushButton#SuccessBtn {
    background-color: #27AE60;
    color: #FFFFFF;
}
QPushButton#SuccessBtn:hover {
    background-color: #2ECC71;
}
QPushButton#DangerBtn {
    background-color: #A93226;
    color: #FFFFFF;
}
QPushButton#DangerBtn:hover {
    background-color: #C0392B;
}
QLineEdit {
    background-color: #21242D;
    border: 1px solid #313543;
    border-radius: 5px;
    padding: 8px 12px;
    color: #FFFFFF;
}
QLineEdit:focus {
    border: 1px solid #DA7C21;
}
QTableWidget {
    background-color: #21242D;
    border: 1px solid #313543;
    border-radius: 6px;
    gridline-color: #2A2E3B;
    selection-background-color: #313543;
    selection-color: #FFFFFF;
}
QHeaderView::section {
    background-color: #262935;
    color: #9A9EAB;
    padding: 8px 10px;
    border: none;
    border-right: 1px solid #313543;
    border-bottom: 1px solid #313543;
    font-weight: bold;
}
QTextEdit {
    background-color: #15171C;
    border: 1px solid #313543;
    border-radius: 6px;
    color: #E2E4E9;
    font-family: 'Consolas', 'Courier New', monospace;
    font-size: 12px;
    padding: 10px;
}
QProgressBar {
    background-color: #21242D;
    border: 1px solid #313543;
    border-radius: 4px;
    text-align: center;
    color: #FFFFFF;
    font-size: 11px;
}
QProgressBar::chunk {
    background-color: #DA7C21;
    border-radius: 3px;
}
"""


class AsyncWorker(QThread):
    finished = pyqtSignal(object)
    failed = pyqtSignal(str)

    def __init__(self, coro):
        super().__init__()
        self.coro = coro

    def run(self):
        try:
            loop = asyncio.new_event_loop()
            asyncio.set_event_loop(loop)
            result = loop.run_until_complete(self.coro)
            loop.close()
            self.finished.emit(result)
        except Exception as e:
            self.failed.emit(str(e))


class DownloadWorker(QThread):
    progress = pyqtSignal(int, int)
    finished = pyqtSignal(str)
    failed = pyqtSignal(str)

    def __init__(self, nexus_api: NexusAPI, uri: str, dest_dir: str):
        super().__init__()
        self.nexus_api = nexus_api
        self.uri = uri
        self.dest_dir = dest_dir

    def run(self):
        try:
            out_file = self.nexus_api.download_file(
                self.uri, self.dest_dir,
                progress_callback=lambda dl, tot: self.progress.emit(dl, tot)
            )
            self.finished.emit(out_file)
        except Exception as e:
            self.failed.emit(str(e))


class ModManagerWindow(QMainWindow):
    nxm_received = pyqtSignal(str)

    def __init__(self, initial_nxm: Optional[str] = None):
        super().__init__()
        self.setWindowTitle("Stardew Valley iOS Mod Manager — Vortex Edition")
        self.resize(1120, 750)
        self.setStyleSheet(DARK_STYLE)
        self.setAcceptDrops(True)

        self.backend = IOSModBackend()
        self.config = load_config()
        self.nexus_api = NexusAPI(self.config.get("nexus_api_key", ""))
        self.mods_cache: list[ModInfo] = []

        self.nxm_received.connect(self._handle_nxm_url)

        self._build_ui()
        self._start_ipc_server()
        self._auto_connect_usb()

        if initial_nxm:
            QTimer.singleShot(1000, lambda: self._handle_nxm_url(initial_nxm))

    def _start_ipc_server(self):
        self.tcp_server = QTcpServer(self)
        if self.tcp_server.listen(QHostAddress.SpecialAddress.LocalHost, IPC_PORT):
            self.tcp_server.newConnection.connect(self._on_ipc_connection)

    def _on_ipc_connection(self):
        client = self.tcp_server.nextPendingConnection()
        if client:
            client.readyRead.connect(lambda: self._read_ipc_data(client))

    def _read_ipc_data(self, client):
        data = client.readAll().data().decode("utf-8", errors="ignore").strip()
        client.disconnectFromHost()
        if data:
            self.activateWindow()
            self.raise_()
            self.nxm_received.emit(data)

    def _build_ui(self):
        central_widget = QWidget()
        self.setCentralWidget(central_widget)
        main_layout = QVBoxLayout(central_widget)
        main_layout.setContentsMargins(0, 0, 0, 0)
        main_layout.setSpacing(0)

        # 1. Header Bar
        header = QFrame()
        header.setObjectName("HeaderBar")
        h_layout = QHBoxLayout(header)
        h_layout.setContentsMargins(16, 12, 16, 12)

        title_box = QVBoxLayout()
        title_lbl = QLabel("STARDEW VALLEY iOS")
        title_lbl.setStyleSheet("font-weight: 900; font-size: 16px; color: #DA7C21; letter-spacing: 1px;")
        sub_lbl = QLabel("Vortex & Nexus Mods Desktop Manager")
        sub_lbl.setStyleSheet("font-size: 11px; color: #8F94A6;")
        title_box.addWidget(title_lbl)
        title_box.addWidget(sub_lbl)
        h_layout.addLayout(title_box)

        h_layout.addStretch()

        self.device_badge = QLabel("🔍 Checking for iOS Device...")
        self.device_badge.setStyleSheet("background-color: #2D303E; color: #DA7C21; padding: 6px 14px; border-radius: 14px; font-weight: bold; font-size: 12px;")
        h_layout.addWidget(self.device_badge)

        self.btn_refresh_dev = QPushButton("⟳ Reconnect USB")
        self.btn_refresh_dev.setObjectName("SecondaryBtn")
        self.btn_refresh_dev.clicked.connect(self._auto_connect_usb)
        h_layout.addWidget(self.btn_refresh_dev)

        self.btn_local_folder = QPushButton("📁 Local Folder")
        self.btn_local_folder.setObjectName("SecondaryBtn")
        self.btn_local_folder.clicked.connect(self._select_local_folder)
        h_layout.addWidget(self.btn_local_folder)

        main_layout.addWidget(header)

        # Download / install progress bar
        self.progress_bar = QProgressBar()
        self.progress_bar.setFixedHeight(14)
        self.progress_bar.setVisible(False)
        main_layout.addWidget(self.progress_bar)

        # 2. Tabs
        self.tabs = QTabWidget()
        self.tabs.setContentsMargins(16, 12, 16, 12)

        self.tab_mods = QWidget()
        self.tab_vortex = QWidget()
        self.tab_nexus = QWidget()
        self.tab_logs = QWidget()
        self.tab_saves = QWidget()
        self.tab_info = QWidget()

        self.tabs.addTab(self.tab_mods, "  🎮 INSTALLED MODS  ")
        self.tabs.addTab(self.tab_vortex, "  🌪️ VORTEX LIBRARY  ")
        self.tabs.addTab(self.tab_nexus, "  🌐 NEXUS DOWNLOADS  ")
        self.tabs.addTab(self.tab_logs, "  📋 SMAPI LOGS  ")
        self.tabs.addTab(self.tab_saves, "  💾 SAVES  ")
        self.tabs.addTab(self.tab_info, "  ⚙️ DIAGNOSTICS  ")

        self._setup_mods_tab()
        self._setup_vortex_tab()
        self._setup_nexus_tab()
        self._setup_logs_tab()
        self._setup_saves_tab()
        self._setup_info_tab()

        main_layout.addWidget(self.tabs)

        # 3. Status Bar
        self.status_bar = QLabel(" Ready")
        self.status_bar.setStyleSheet("background-color: #191B21; color: #8F94A6; padding: 6px 16px; font-size: 11px;")
        main_layout.addWidget(self.status_bar)

    # ----------------- 1. INSTALLED MODS TAB -----------------
    def _setup_mods_tab(self):
        layout = QVBoxLayout(self.tab_mods)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(12)

        toolbar = QHBoxLayout()
        self.btn_install_mod = QPushButton("📥 Install Mod (.zip / folder)")
        self.btn_install_mod.clicked.connect(self._browse_and_install_mod)
        toolbar.addWidget(self.btn_install_mod)

        self.btn_refresh_mods = QPushButton("⟳ Refresh")
        self.btn_refresh_mods.setObjectName("SecondaryBtn")
        self.btn_refresh_mods.clicked.connect(self._refresh_mods)
        toolbar.addWidget(self.btn_refresh_mods)

        toolbar.addSpacing(16)

        self.search_box = QLineEdit()
        self.search_box.setPlaceholderText("🔍 Filter installed mods by name, author, or unique ID...")
        self.search_box.textChanged.connect(self._filter_mods)
        toolbar.addWidget(self.search_box)

        layout.addLayout(toolbar)

        self.mod_table = QTableWidget()
        self.mod_table.setColumnCount(6)
        self.mod_table.setHorizontalHeaderLabels(["Status", "Mod Name", "Version", "Author", "Type", "Actions"])
        self.mod_table.horizontalHeader().setSectionResizeMode(0, QHeaderView.ResizeMode.ResizeToContents)
        self.mod_table.horizontalHeader().setSectionResizeMode(1, QHeaderView.ResizeMode.Stretch)
        self.mod_table.horizontalHeader().setSectionResizeMode(2, QHeaderView.ResizeMode.ResizeToContents)
        self.mod_table.horizontalHeader().setSectionResizeMode(3, QHeaderView.ResizeMode.ResizeToContents)
        self.mod_table.horizontalHeader().setSectionResizeMode(4, QHeaderView.ResizeMode.ResizeToContents)
        self.mod_table.horizontalHeader().setSectionResizeMode(5, QHeaderView.ResizeMode.ResizeToContents)
        self.mod_table.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        self.mod_table.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        self.mod_table.verticalHeader().setVisible(False)
        layout.addWidget(self.mod_table)

        info_row = QHBoxLayout()
        self.lbl_mod_stats = QLabel("Total: 0 mods")
        self.lbl_mod_stats.setStyleSheet("color: #8F94A6; font-size: 12px;")
        info_row.addWidget(self.lbl_mod_stats)

        info_row.addStretch()

        self.lbl_cp_status = QLabel("Content Patcher: Checking...")
        self.lbl_cp_status.setStyleSheet("color: #F1C40F; font-size: 12px; font-weight: bold;")
        info_row.addWidget(self.lbl_cp_status)

        layout.addLayout(info_row)

    # ----------------- 2. VORTEX LIBRARY TAB -----------------
    def _setup_vortex_tab(self):
        layout = QVBoxLayout(self.tab_vortex)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(12)

        desc = QLabel("Detected Stardew Valley mods in your local Vortex collection. Select any mod to push it directly to your iOS device over USB.")
        desc.setStyleSheet("color: #9A9EAB; font-size: 12px;")
        layout.addWidget(desc)

        toolbar = QHBoxLayout()
        self.btn_refresh_vortex = QPushButton("⟳ Scan Vortex Mods")
        self.btn_refresh_vortex.setObjectName("SecondaryBtn")
        self.btn_refresh_vortex.clicked.connect(self._scan_vortex_mods)
        toolbar.addWidget(self.btn_refresh_vortex)

        self.btn_sync_vortex = QPushButton("⚡ Install Selected to iPhone")
        self.btn_sync_vortex.setObjectName("SuccessBtn")
        self.btn_sync_vortex.clicked.connect(self._install_selected_vortex_mods)
        toolbar.addWidget(self.btn_sync_vortex)

        toolbar.addSpacing(16)
        self.vortex_search = QLineEdit()
        self.vortex_search.setPlaceholderText("🔍 Filter Vortex mods...")
        self.vortex_search.textChanged.connect(self._filter_vortex_table)
        toolbar.addWidget(self.vortex_search)

        layout.addLayout(toolbar)

        self.vortex_table = QTableWidget()
        self.vortex_table.setColumnCount(4)
        self.vortex_table.setHorizontalHeaderLabels(["Select", "Mod / Archive Name", "Type / Source", "Action"])
        self.vortex_table.horizontalHeader().setSectionResizeMode(0, QHeaderView.ResizeMode.ResizeToContents)
        self.vortex_table.horizontalHeader().setSectionResizeMode(1, QHeaderView.ResizeMode.Stretch)
        self.vortex_table.horizontalHeader().setSectionResizeMode(2, QHeaderView.ResizeMode.ResizeToContents)
        self.vortex_table.horizontalHeader().setSectionResizeMode(3, QHeaderView.ResizeMode.ResizeToContents)
        self.vortex_table.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        self.vortex_table.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        self.vortex_table.verticalHeader().setVisible(False)
        layout.addWidget(self.vortex_table)

        self.vortex_items_cache = []
        QTimer.singleShot(500, self._scan_vortex_mods)

    def _scan_vortex_mods(self):
        mods_dir, dl_dir = get_vortex_stardew_dirs()
        self.vortex_items_cache = []

        # 1. Scanned staged mods
        if mods_dir and os.path.isdir(mods_dir):
            for d in os.listdir(mods_dir):
                full_p = os.path.join(mods_dir, d)
                if os.path.isdir(full_p):
                    self.vortex_items_cache.append({
                        "name": d,
                        "path": full_p,
                        "type": "Vortex Staged Folder",
                        "is_dir": True
                    })

        # 2. Scanned downloaded archives
        if dl_dir and os.path.isdir(dl_dir):
            for f in os.listdir(dl_dir):
                if f.endswith((".zip", ".7z", ".rar")) and not f.startswith("__vortex"):
                    full_p = os.path.join(dl_dir, f)
                    self.vortex_items_cache.append({
                        "name": f,
                        "path": full_p,
                        "type": "Downloaded Archive (.zip)",
                        "is_dir": False
                    })

        self._filter_vortex_table()

    def _filter_vortex_table(self):
        query = self.vortex_search.text().strip().lower()
        filtered = [item for item in self.vortex_items_cache if not query or query in item["name"].lower()]

        self.vortex_table.setRowCount(len(filtered))
        for row, item in enumerate(filtered):
            # Checkbox
            chk = QCheckBox()
            cell_w = QWidget()
            l = QHBoxLayout(cell_w)
            l.addWidget(chk)
            l.setAlignment(Qt.AlignmentFlag.AlignCenter)
            l.setContentsMargins(4, 2, 4, 2)
            self.vortex_table.setCellWidget(row, 0, cell_w)

            # Name
            name_item = QTableWidgetItem(item["name"])
            self.vortex_table.setItem(row, 1, name_item)

            # Type
            type_item = QTableWidgetItem(item["type"])
            type_item.setForeground(QColor("#DA7C21" if item["is_dir"] else "#3498DB"))
            self.vortex_table.setItem(row, 2, type_item)

            # Action button
            btn = QPushButton("Install")
            btn.setFixedHeight(24)
            btn.clicked.connect(lambda _, p=item["path"]: self._install_mod(p))
            act_w = QWidget()
            al = QHBoxLayout(act_w)
            al.addWidget(btn)
            al.setAlignment(Qt.AlignmentFlag.AlignCenter)
            al.setContentsMargins(4, 2, 4, 2)
            self.vortex_table.setCellWidget(row, 3, act_w)

    def _install_selected_vortex_mods(self):
        if not self.backend.is_connected:
            QMessageBox.warning(self, "Not Connected", "Please connect your iOS device via USB first.")
            return

        selected_paths = []
        for row in range(self.vortex_table.rowCount()):
            cell = self.vortex_table.cellWidget(row, 0)
            if cell:
                chk = cell.findChild(QCheckBox)
                if chk and chk.isChecked():
                    name = self.vortex_table.item(row, 1).text()
                    for item in self.vortex_items_cache:
                        if item["name"] == name:
                            selected_paths.append(item["path"])
                            break

        if not selected_paths:
            QMessageBox.information(self, "No Selection", "Please check the box next to the mod(s) you wish to install.")
            return

        # Install sequentially
        self._batch_install_paths(selected_paths)

    def _batch_install_paths(self, paths: list[str]):
        if not paths:
            self._refresh_mods()
            QMessageBox.information(self, "Complete", "Finished installing selected mods!")
            return

        next_path = paths[0]
        remaining = paths[1:]

        self.status_bar.setText(f" Installing {os.path.basename(next_path)} ({len(remaining) + 1} remaining)...")
        worker = AsyncWorker(self.backend.install_mod_archive(next_path))
        worker.finished.connect(lambda res: self._batch_install_paths(remaining))
        worker.failed.connect(lambda err: self._batch_install_paths(remaining))
        self._current_worker = worker
        worker.start()

    # ----------------- 3. NEXUS DOWNLOADS TAB -----------------
    def _setup_nexus_tab(self):
        layout = QVBoxLayout(self.tab_nexus)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(16)

        # API Key Card
        api_card = QFrame()
        api_card.setStyleSheet("background-color: #21242D; border: 1px solid #313543; border-radius: 6px; padding: 14px;")
        api_layout = QVBoxLayout(api_card)
        api_layout.setSpacing(10)

        card_title = QLabel("🔑 Nexus Mods API Configuration")
        card_title.setStyleSheet("font-weight: bold; font-size: 14px; color: #DA7C21;")
        api_layout.addWidget(card_title)

        api_desc = QLabel("Enter your Nexus Mods Personal API Key to enable 1-click downloads directly into your iPhone.\nFind your key at: https://www.nexusmods.com/users/myaccount?tab=api (Personal API Key section)")
        api_desc.setStyleSheet("color: #9A9EAB; font-size: 12px;")
        api_layout.addWidget(api_desc)

        key_row = QHBoxLayout()
        self.api_key_input = QLineEdit()
        self.api_key_input.setPlaceholderText("Paste your Personal API Key here...")
        self.api_key_input.setEchoMode(QLineEdit.EchoMode.Password)
        if self.nexus_api.api_key:
            self.api_key_input.setText(self.nexus_api.api_key)
        key_row.addWidget(self.api_key_input)

        self.btn_save_key = QPushButton("Save & Validate Key")
        self.btn_save_key.clicked.connect(self._validate_and_save_api_key)
        key_row.addWidget(self.btn_save_key)

        api_layout.addLayout(key_row)

        self.lbl_nexus_user = QLabel("Status: Not validated")
        self.lbl_nexus_user.setStyleSheet("font-size: 12px; color: #8F94A6;")
        api_layout.addWidget(self.lbl_nexus_user)

        layout.addWidget(api_card)

        # NXM Protocol Registration Card
        nxm_card = QFrame()
        nxm_card.setStyleSheet("background-color: #21242D; border: 1px solid #313543; border-radius: 6px; padding: 14px;")
        nxm_layout = QVBoxLayout(nxm_card)
        nxm_layout.setSpacing(10)

        nxm_title = QLabel("🔗 Nexus One-Click Protocol (nxm://)")
        nxm_title.setStyleSheet("font-weight: bold; font-size: 14px; color: #DA7C21;")
        nxm_layout.addWidget(nxm_title)

        nxm_desc = QLabel("Associate with 'nxm://' protocol so clicking 'MOD MANAGER DOWNLOAD' on Nexus Mods automatically downloads and installs the mod straight to your iPhone.")
        nxm_desc.setStyleSheet("color: #9A9EAB; font-size: 12px;")
        nxm_layout.addWidget(nxm_desc)

        reg_row = QHBoxLayout()
        self.lbl_nxm_status = QLabel("Protocol Status: Checking...")
        self.lbl_nxm_status.setStyleSheet("font-weight: bold; font-size: 12px;")
        reg_row.addWidget(self.lbl_nxm_status)

        reg_row.addStretch()

        self.btn_reg_nxm = QPushButton("Register nxm:// Handler")
        self.btn_reg_nxm.setObjectName("SecondaryBtn")
        self.btn_reg_nxm.clicked.connect(self._toggle_nxm_registration)
        reg_row.addWidget(self.btn_reg_nxm)

        nxm_layout.addLayout(reg_row)
        layout.addWidget(nxm_card)

        # Manual NXM URL Input
        man_card = QFrame()
        man_card.setStyleSheet("background-color: #21242D; border: 1px solid #313543; border-radius: 6px; padding: 14px;")
        man_layout = QVBoxLayout(man_card)
        man_layout.setSpacing(10)

        man_title = QLabel("📥 Manual NXM Link Downloader")
        man_title.setStyleSheet("font-weight: bold; font-size: 14px; color: #DA7C21;")
        man_layout.addWidget(man_title)

        man_row = QHBoxLayout()
        self.nxm_input = QLineEdit()
        self.nxm_input.setPlaceholderText("Paste nxm:// link here if not clicked from browser...")
        man_row.addWidget(self.nxm_input)

        self.btn_download_nxm = QPushButton("Download & Install")
        self.btn_download_nxm.clicked.connect(lambda: self._handle_nxm_url(self.nxm_input.text().strip()))
        man_row.addWidget(self.btn_download_nxm)

        man_layout.addLayout(man_row)
        layout.addWidget(man_card)

        layout.addStretch()

        # Update initial states
        self._update_nxm_status()
        if self.nexus_api.api_key:
            QTimer.singleShot(600, self._validate_and_save_api_key)

    def _validate_and_save_api_key(self):
        key = self.api_key_input.text().strip()
        if not key:
            QMessageBox.warning(self, "Empty Key", "Please paste your Nexus Mods Personal API Key.")
            return

        self.nexus_api.set_api_key(key)
        self.lbl_nexus_user.setText("Validating key with Nexus Mods...")
        self.lbl_nexus_user.setStyleSheet("color: #F1C40F;")

        def check():
            ok, data = self.nexus_api.validate_api_key()
            return ok, data

        worker = AsyncWorker(asyncio.to_thread(check))
        worker.finished.connect(self._on_key_validated)
        worker.failed.connect(lambda e: self.lbl_nexus_user.setText(f"Validation error: {e}"))
        self._current_worker = worker
        worker.start()

    def _on_key_validated(self, res):
        ok, data = res
        if ok:
            user_name = data.get("name", "User")
            is_prem = data.get("is_premium", False)
            prem_tag = "Premium" if is_prem else "Standard"
            self.lbl_nexus_user.setText(f"🟢 Connected as: {user_name} ({prem_tag})")
            self.lbl_nexus_user.setStyleSheet("color: #2ECC71; font-weight: bold;")
            self.config["nexus_api_key"] = self.nexus_api.api_key
            self.config["user_name"] = user_name
            save_config(self.config)
        else:
            msg = data.get("message", "Invalid API Key")
            self.lbl_nexus_user.setText(f"🔴 {msg}")
            self.lbl_nexus_user.setStyleSheet("color: #E74C3C; font-weight: bold;")

    def _update_nxm_status(self):
        if is_nxm_registered_to_us():
            self.lbl_nxm_status.setText("🟢 Registered to SDV iOS Mod Manager")
            self.lbl_nxm_status.setStyleSheet("color: #2ECC71;")
            self.btn_reg_nxm.setText("✓ Registered")
            self.btn_reg_nxm.setEnabled(False)
        else:
            self.lbl_nxm_status.setText("⚪ Not currently registered to this tool (Points to Vortex/Other)")
            self.lbl_nxm_status.setStyleSheet("color: #8F94A6;")
            self.btn_reg_nxm.setText("Register nxm:// Handler")
            self.btn_reg_nxm.setEnabled(True)

    def _toggle_nxm_registration(self):
        if register_nxm_protocol():
            QMessageBox.information(self, "Success", "Successfully associated 'nxm://' links with Stardew Valley iOS Mod Manager!")
            self._update_nxm_status()
        else:
            QMessageBox.warning(self, "Error", "Could not register nxm:// protocol handler.")

    def _handle_nxm_url(self, nxm_url: str):
        if not nxm_url.startswith("nxm://"):
            return

        if not self.nexus_api.api_key:
            self.tabs.setCurrentIndex(2)
            QMessageBox.warning(self, "API Key Required", "Please configure your Nexus Mods API Key first under the 'Nexus Downloads' tab.")
            return

        parsed = self.nexus_api.parse_nxm_url(nxm_url)
        if not parsed:
            QMessageBox.warning(self, "Invalid URL", f"Could not parse NXM link: {nxm_url}")
            return

        self.tabs.setCurrentIndex(2)
        self.progress_bar.setVisible(True)
        self.progress_bar.setRange(0, 0)
        self.status_bar.setText(f" Resolving download link for Mod #{parsed['mod_id']}...")

        def fetch_links():
            return self.nexus_api.get_download_links(
                parsed["game"], parsed["mod_id"], parsed["file_id"],
                parsed["key"], parsed["expires"]
            )

        worker = AsyncWorker(asyncio.to_thread(fetch_links))
        worker.finished.connect(lambda links: self._on_links_resolved(links, parsed))
        worker.failed.connect(self._on_nxm_failed)
        self._current_worker = worker
        worker.start()

    def _on_links_resolved(self, links: list[str], parsed: dict):
        if not links:
            self.progress_bar.setVisible(False)
            QMessageBox.warning(self, "Download Error", "No download links returned by Nexus Mods API.")
            return

        cdn_uri = links[0]
        self.status_bar.setText(f" Downloading mod file from Nexus CDN...")
        self.progress_bar.setRange(0, 100)
        self.progress_bar.setValue(0)

        dl_worker = DownloadWorker(self.nexus_api, cdn_uri, DEFAULT_DOWNLOAD_DIR)
        dl_worker.progress.connect(self._on_download_progress)
        dl_worker.finished.connect(self._on_download_finished)
        dl_worker.failed.connect(self._on_nxm_failed)
        self._current_dl_worker = dl_worker
        dl_worker.start()

    def _on_download_progress(self, dl: int, tot: int):
        if tot > 0:
            pct = int((dl / tot) * 100)
            self.progress_bar.setValue(pct)
            mb_dl = dl / (1024 * 1024)
            mb_tot = tot / (1024 * 1024)
            self.status_bar.setText(f" Downloading from Nexus: {mb_dl:.1f} MB / {mb_tot:.1f} MB ({pct}%)")

    def _on_download_finished(self, file_path: str):
        self.progress_bar.setRange(0, 0)
        self.status_bar.setText(f" Installing {os.path.basename(file_path)} to iPhone...")
        self._install_mod(file_path)

    def _on_nxm_failed(self, err: str):
        self.progress_bar.setVisible(False)
        QMessageBox.critical(self, "Nexus Error", f"Failed to process NXM link:\n{err}")

    # ----------------- 4. LOGS TAB -----------------
    def _setup_logs_tab(self):
        layout = QVBoxLayout(self.tab_logs)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(12)

        toolbar = QHBoxLayout()
        self.btn_fetch_log = QPushButton("⟳ Fetch Latest SMAPI Log")
        self.btn_fetch_log.clicked.connect(self._fetch_smapi_log)
        toolbar.addWidget(self.btn_fetch_log)

        self.btn_copy_log = QPushButton("📋 Copy to Clipboard")
        self.btn_copy_log.setObjectName("SecondaryBtn")
        self.btn_copy_log.clicked.connect(self._copy_log)
        toolbar.addWidget(self.btn_copy_log)

        self.btn_save_log = QPushButton("💾 Save Log to File")
        self.btn_save_log.setObjectName("SecondaryBtn")
        self.btn_save_log.clicked.connect(self._save_log)
        toolbar.addWidget(self.btn_save_log)

        toolbar.addStretch()
        layout.addLayout(toolbar)

        self.log_viewer = QTextEdit()
        self.log_viewer.setReadOnly(True)
        self.log_viewer.setPlaceholderText("Click 'Fetch Latest SMAPI Log' to stream logs directly from your iPhone over USB...")
        layout.addWidget(self.log_viewer)

    def _fetch_smapi_log(self):
        if not self.backend.is_connected:
            QMessageBox.warning(self, "Not Connected", "Please connect your device first.")
            return

        self.status_bar.setText(" Fetching SMAPI log from device...")
        worker = AsyncWorker(self.backend.get_smapi_log())
        worker.finished.connect(lambda text: self.log_viewer.setPlainText(text))
        worker.failed.connect(lambda e: self.log_viewer.setPlainText(f"Error fetching log: {e}"))
        self._current_worker = worker
        worker.start()

    def _copy_log(self):
        text = self.log_viewer.toPlainText()
        if text:
            QApplication.clipboard().setText(text)
            self.status_bar.setText(" Log copied to clipboard.")

    def _save_log(self):
        text = self.log_viewer.toPlainText()
        if not text:
            return
        path, _ = QFileDialog.getSaveFileName(self, "Save SMAPI Log", "SMAPI-latest.txt", "Text Files (*.txt)")
        if path:
            with open(path, "w", encoding="utf-8") as f:
                f.write(text)
            QMessageBox.information(self, "Saved", f"Log saved to: {path}")

    # ----------------- 5. SAVES TAB -----------------
    def _setup_saves_tab(self):
        layout = QVBoxLayout(self.tab_saves)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(12)

        toolbar = QHBoxLayout()
        self.btn_refresh_saves = QPushButton("⟳ Refresh Saves")
        self.btn_refresh_saves.setObjectName("SecondaryBtn")
        self.btn_refresh_saves.clicked.connect(self._refresh_saves)
        toolbar.addWidget(self.btn_refresh_saves)

        self.btn_backup_save = QPushButton("💾 Backup Selected Save (.zip)")
        self.btn_backup_save.clicked.connect(self._backup_selected_save)
        toolbar.addWidget(self.btn_backup_save)

        toolbar.addStretch()
        layout.addLayout(toolbar)

        self.saves_table = QTableWidget()
        self.saves_table.setColumnCount(2)
        self.saves_table.setHorizontalHeaderLabels(["Save Folder Name", "Status"])
        self.saves_table.horizontalHeader().setSectionResizeMode(0, QHeaderView.ResizeMode.Stretch)
        self.saves_table.horizontalHeader().setSectionResizeMode(1, QHeaderView.ResizeMode.ResizeToContents)
        self.saves_table.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        self.saves_table.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        self.saves_table.verticalHeader().setVisible(False)
        layout.addWidget(self.saves_table)

    def _refresh_saves(self):
        if not self.backend.is_connected:
            return
        self.status_bar.setText(" Listing save games...")
        worker = AsyncWorker(self.backend.list_saves())
        worker.finished.connect(self._render_saves)
        worker.failed.connect(lambda e: self.status_bar.setText(f" Error listing saves: {e}"))
        self._current_worker = worker
        worker.start()

    def _render_saves(self, saves: list[str]):
        self.saves_table.setRowCount(len(saves))
        for row, s in enumerate(saves):
            name_item = QTableWidgetItem(s)
            name_item.setFont(QFont("Segoe UI", 10, QFont.Weight.Bold))
            self.saves_table.setItem(row, 0, name_item)

            stat_item = QTableWidgetItem("Ready")
            stat_item.setTextAlignment(Qt.AlignmentFlag.AlignCenter)
            stat_item.setForeground(QColor("#2ECC71"))
            self.saves_table.setItem(row, 1, stat_item)
        self.status_bar.setText(f" Found {len(saves)} save games on device.")

    def _backup_selected_save(self):
        row = self.saves_table.currentRow()
        if row < 0:
            QMessageBox.information(self, "Select Save", "Please select a save game from the list to backup.")
            return

        save_name = self.saves_table.item(row, 0).text()
        dest_dir = QFileDialog.getExistingDirectory(self, "Select Backup Destination Directory")
        if not dest_dir:
            return

        self.status_bar.setText(f" Backing up save {save_name}...")
        worker = AsyncWorker(self.backend.backup_save(save_name, dest_dir))
        worker.finished.connect(lambda res: QMessageBox.information(self, "Backup Complete", res[1]))
        worker.failed.connect(lambda err: QMessageBox.warning(self, "Backup Error", f"Backup failed: {err}"))
        self._current_worker = worker
        worker.start()

    # ----------------- 6. DIAGNOSTICS TAB -----------------
    def _setup_info_tab(self):
        layout = QVBoxLayout(self.tab_info)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(16)

        self.info_text = QTextEdit()
        self.info_text.setReadOnly(True)
        layout.addWidget(self.info_text)

    def _update_diagnostics(self):
        lines = []
        lines.append("=== STARDEW VALLEY iOS DEVICE DIAGNOSTICS ===")
        lines.append(f"Connection Type: {'Local Folder' if self.backend.local_mode_dir else 'USB (Apple AFC)'}")
        if self.backend.device_info:
            for k, v in self.backend.device_info.items():
                lines.append(f"{k}: {v}")
        if self.backend.bundle_id:
            lines.append(f"App Bundle ID: {self.backend.bundle_id}")
        if self.backend.local_mode_dir:
            lines.append(f"Local Directory: {self.backend.local_mode_dir}")

        lines.append("\n=== VORTEX CONFIGURATION ===")
        mods_dir, dl_dir = get_vortex_stardew_dirs()
        lines.append(f"Vortex Mods Path: {mods_dir}")
        lines.append(f"Vortex Downloads Path: {dl_dir}")

        lines.append("\n=== NEXUS MODS INTEGRATION ===")
        lines.append(f"API Key Configured: {'Yes' if self.nexus_api.api_key else 'No'}")
        lines.append(f"NXM Protocol Registered to Tool: {'Yes' if is_nxm_registered_to_us() else 'No'}")

        self.info_text.setPlainText("\n".join(lines))

    # ----------------- DEVICE CONNECTION -----------------
    def _auto_connect_usb(self):
        self.device_badge.setText("🔍 Connecting to USB...")
        self.device_badge.setStyleSheet("background-color: #2D303E; color: #DA7C21; padding: 6px 14px; border-radius: 14px; font-weight: bold; font-size: 12px;")
        self.status_bar.setText(" Connecting to iPhone over Apple USB AFC...")

        worker = AsyncWorker(self.backend.connect_usb())
        worker.finished.connect(self._on_usb_connected)
        worker.failed.connect(self._on_usb_failed)
        self._current_worker = worker
        worker.start()

    def _on_usb_connected(self, result):
        ok, msg = result
        if ok:
            dev_name = self.backend.device_info.get("DeviceName", "iOS Device")
            model = self.backend.device_info.get("ProductType", "iPhone")
            os_ver = self.backend.device_info.get("ProductVersion", "iOS")
            self.device_badge.setText(f"🟢 {dev_name} ({model}, iOS {os_ver})")
            self.device_badge.setStyleSheet("background-color: #1A3A28; color: #2ECC71; padding: 6px 14px; border-radius: 14px; font-weight: bold; font-size: 12px;")
            self.status_bar.setText(f" Connected: {msg}")
            self._update_diagnostics()
            self._refresh_mods()
        else:
            self.device_badge.setText("🔴 No Device Connected")
            self.device_badge.setStyleSheet("background-color: #3A1E1E; color: #E74C3C; padding: 6px 14px; border-radius: 14px; font-weight: bold; font-size: 12px;")
            self.status_bar.setText(f" Connection failed: {msg}")

    def _on_usb_failed(self, err):
        self.device_badge.setText("🔴 USB Error")
        self.device_badge.setStyleSheet("background-color: #3A1E1E; color: #E74C3C; padding: 6px 14px; border-radius: 14px; font-weight: bold; font-size: 12px;")
        self.status_bar.setText(f" USB Error: {err}")

    def _select_local_folder(self):
        folder = QFileDialog.getExistingDirectory(self, "Select Stardew Valley App Documents / Mods Folder")
        if folder:
            ok, msg = self.backend.connect_local_folder(folder)
            if ok:
                self.device_badge.setText("📁 Local Folder Mode")
                self.device_badge.setStyleSheet("background-color: #2D303E; color: #3498DB; padding: 6px 14px; border-radius: 14px; font-weight: bold; font-size: 12px;")
                self.status_bar.setText(f" Connected to local folder: {folder}")
                self._update_diagnostics()
                self._refresh_mods()
            else:
                QMessageBox.warning(self, "Error", msg)

    # ----------------- MODS MANAGEMENT -----------------
    def _refresh_mods(self):
        if not self.backend.is_connected:
            return
        self.status_bar.setText(" Fetching installed mods list...")
        worker = AsyncWorker(self.backend.list_mods())
        worker.finished.connect(self._render_mods)
        worker.failed.connect(lambda e: self.status_bar.setText(f" Error fetching mods: {e}"))
        self._current_worker = worker
        worker.start()

    def _render_mods(self, mods: list[ModInfo]):
        self.mods_cache = mods
        self._filter_mods()

    def _filter_mods(self):
        query = self.search_box.text().strip().lower()
        filtered = [
            m for m in self.mods_cache
            if not query or query in m.name.lower() or query in m.author.lower() or query in m.unique_id.lower()
        ]

        self.mod_table.setRowCount(len(filtered))
        has_content_patcher = False

        for row, mod in enumerate(filtered):
            if "contentpatcher" in mod.unique_id.lower() or "content patcher" in mod.name.lower():
                has_content_patcher = True

            # Col 0: Checkbox
            chk = QCheckBox()
            chk.setChecked(mod.is_enabled)
            chk.setText(" Enabled" if mod.is_enabled else " Disabled")
            chk.setStyleSheet("color: #2ECC71; font-weight: bold;" if mod.is_enabled else "color: #7F8C8D;")
            chk.clicked.connect(lambda checked, m=mod: self._toggle_mod(m))
            cell_widget = QWidget()
            cell_layout = QHBoxLayout(cell_widget)
            cell_layout.addWidget(chk)
            cell_layout.setAlignment(Qt.AlignmentFlag.AlignCenter)
            cell_layout.setContentsMargins(4, 2, 4, 2)
            self.mod_table.setCellWidget(row, 0, cell_widget)

            # Col 1: Name
            name_item = QTableWidgetItem(mod.name)
            name_item.setFont(QFont("Segoe UI", 10, QFont.Weight.Bold))
            if not mod.is_enabled:
                name_item.setForeground(QColor("#7F8C8D"))
            self.mod_table.setItem(row, 1, name_item)

            # Col 2: Version
            ver_item = QTableWidgetItem(mod.version)
            ver_item.setTextAlignment(Qt.AlignmentFlag.AlignCenter)
            self.mod_table.setItem(row, 2, ver_item)

            # Col 3: Author
            auth_item = QTableWidgetItem(mod.author)
            self.mod_table.setItem(row, 3, auth_item)

            # Col 4: Type
            type_str = "Content Pack" if mod.is_content_pack else ("C# Code Mod" if mod.is_code_mod else "Standard Mod")
            type_item = QTableWidgetItem(type_str)
            type_item.setTextAlignment(Qt.AlignmentFlag.AlignCenter)
            type_item.setForeground(QColor("#DA7C21" if mod.is_content_pack else "#3498DB"))
            self.mod_table.setItem(row, 4, type_item)

            # Col 5: Delete
            btn_del = QPushButton("Delete")
            btn_del.setObjectName("DangerBtn")
            btn_del.setFixedHeight(26)
            btn_del.clicked.connect(lambda _, m=mod: self._delete_mod(m))
            del_widget = QWidget()
            del_layout = QHBoxLayout(del_widget)
            del_layout.addWidget(btn_del)
            del_layout.setAlignment(Qt.AlignmentFlag.AlignCenter)
            del_layout.setContentsMargins(4, 2, 4, 2)
            self.mod_table.setCellWidget(row, 5, del_widget)

        enabled_count = sum(1 for m in self.mods_cache if m.is_enabled)
        self.lbl_mod_stats.setText(f"Total: {len(self.mods_cache)} mods ({enabled_count} enabled, {len(self.mods_cache) - enabled_count} disabled)")

        if has_content_patcher:
            self.lbl_cp_status.setText("✓ Content Patcher: INSTALLED")
            self.lbl_cp_status.setStyleSheet("color: #2ECC71; font-size: 12px; font-weight: bold;")
        else:
            self.lbl_cp_status.setText("⚠ Content Patcher: NOT FOUND")
            self.lbl_cp_status.setStyleSheet("color: #F39C12; font-size: 12px; font-weight: bold;")

        self.status_bar.setText(f" Loaded {len(self.mods_cache)} mods from device.")

    def _toggle_mod(self, mod: ModInfo):
        action = "Disabling" if mod.is_enabled else "Enabling"
        self.status_bar.setText(f" {action} {mod.name}...")
        worker = AsyncWorker(self.backend.toggle_mod(mod))
        worker.finished.connect(lambda res: self._refresh_mods())
        worker.failed.connect(lambda err: QMessageBox.warning(self, "Error", f"Failed to toggle mod: {err}"))
        self._current_worker = worker
        worker.start()

    def _delete_mod(self, mod: ModInfo):
        reply = QMessageBox.question(
            self, "Confirm Delete",
            f"Are you sure you want to permanently delete mod '{mod.name}' from your iOS device?",
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No
        )
        if reply == QMessageBox.StandardButton.Yes:
            self.status_bar.setText(f" Deleting {mod.name}...")
            worker = AsyncWorker(self.backend.delete_mod(mod))
            worker.finished.connect(lambda res: self._refresh_mods())
            worker.failed.connect(lambda err: QMessageBox.warning(self, "Error", f"Failed to delete mod: {err}"))
            self._current_worker = worker
            worker.start()

    def _browse_and_install_mod(self):
        if not self.backend.is_connected:
            QMessageBox.warning(self, "Not Connected", "Please connect your iOS device via USB or select a local folder first.")
            return

        file_path, _ = QFileDialog.getOpenFileName(
            self, "Select Mod Archive", "", "Mod Archives (*.zip *.rar *.7z);;All Files (*.*)"
        )
        if file_path:
            self._install_mod(file_path)

    def _install_mod(self, path: str):
        self.progress_bar.setVisible(True)
        self.progress_bar.setRange(0, 0)
        self.status_bar.setText(f" Installing {os.path.basename(path)} to iOS device...")

        worker = AsyncWorker(self.backend.install_mod_archive(path, lambda msg: self.status_bar.setText(f" {msg}")))
        worker.finished.connect(self._on_install_finished)
        worker.failed.connect(self._on_install_failed)
        self._current_worker = worker
        worker.start()

    def _on_install_finished(self, result):
        self.progress_bar.setVisible(False)
        ok, msg = result
        if ok:
            QMessageBox.information(self, "Installation Successful", msg)
            self._refresh_mods()
        else:
            QMessageBox.warning(self, "Installation Failed", msg)

    def _on_install_failed(self, err):
        self.progress_bar.setVisible(False)
        QMessageBox.critical(self, "Error", f"Installation error: {err}")

    # Drag and Drop
    def dragEnterEvent(self, event: QDragEnterEvent):
        if event.mimeData().hasUrls():
            event.acceptProposedAction()

    def dropEvent(self, event: QDropEvent):
        urls = event.mimeData().urls()
        if urls:
            path = urls[0].toLocalFile()
            if os.path.exists(path):
                self._install_mod(path)


def main():
    initial_nxm = sys.argv[1] if len(sys.argv) > 1 and sys.argv[1].startswith("nxm://") else None

    # Check if an instance is already running
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        s.settimeout(0.5)
        s.connect(("127.0.0.1", IPC_PORT))
        if initial_nxm:
            s.sendall(initial_nxm.encode("utf-8"))
        s.close()
        # Already running and forwarded
        sys.exit(0)
    except Exception:
        # No running instance, proceed to launch GUI
        pass

    app = QApplication(sys.argv)
    window = ModManagerWindow(initial_nxm=initial_nxm)
    window.show()
    sys.exit(app.exec())


if __name__ == "__main__":
    main()

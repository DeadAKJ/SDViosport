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
import webbrowser
import urllib.parse
from typing import Optional

from PyQt6.QtWidgets import (
    QApplication, QMainWindow, QWidget, QVBoxLayout, QHBoxLayout,
    QLabel, QPushButton, QTabWidget, QTableWidget, QTableWidgetItem,
    QHeaderView, QFileDialog, QMessageBox, QLineEdit, QTextEdit,
    QProgressBar, QFrame, QSplitter, QCheckBox, QAbstractItemView,
    QDialog, QComboBox
)
from PyQt6.QtCore import Qt, QThread, pyqtSignal, QTimer, QObject
from PyQt6.QtGui import QColor, QFont, QIcon, QDragEnterEvent, QDropEvent
from PyQt6.QtNetwork import QTcpServer, QHostAddress

try:
    from tools.mod_manager.backend import IOSModBackend, ModInfo
    from tools.mod_manager.nexus import (
        NexusAPI, load_config, save_config, register_nxm_protocol,
        is_nxm_registered_to_us, get_vortex_stardew_dirs, DEFAULT_DOWNLOAD_DIR,
        parse_any_url
    )
except ImportError:
    from backend import IOSModBackend, ModInfo
    from nexus import (
        NexusAPI, load_config, save_config, register_nxm_protocol,
        is_nxm_registered_to_us, get_vortex_stardew_dirs, DEFAULT_DOWNLOAD_DIR,
        parse_any_url
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
QComboBox {
    background-color: #21242D;
    border: 1px solid #313543;
    border-radius: 5px;
    padding: 6px 12px;
    color: #FFFFFF;
}
QComboBox:focus {
    border: 1px solid #DA7C21;
}
QComboBox QAbstractItemView {
    background-color: #21242D;
    border: 1px solid #313543;
    selection-background-color: #DA7C21;
    selection-color: #FFFFFF;
    color: #FFFFFF;
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


class AsyncDispatcher(QObject):
    task_done = pyqtSignal(object, object)
    task_failed = pyqtSignal(object, str)

    def __init__(self):
        super().__init__()
        self.loop = asyncio.new_event_loop()
        self.thread = threading.Thread(target=self._run_loop, daemon=True)
        self.thread.start()
        self.task_done.connect(self._handle_done)
        self.task_failed.connect(self._handle_failed)

    def _run_loop(self):
        asyncio.set_event_loop(self.loop)
        self.loop.run_forever()

    def _handle_done(self, cb, res):
        if cb:
            try:
                cb(res)
            except Exception as e:
                print(f"[Dispatcher] Callback error: {e}")

    def _handle_failed(self, err_cb, err_str):
        if err_cb:
            try:
                err_cb(err_str)
            except Exception as e:
                print(f"[Dispatcher] Error callback error: {e}")

    def run_async(self, coro, on_success=None, on_error=None):
        async def runner():
            try:
                res = await coro
                self.task_done.emit(on_success, res)
            except Exception as e:
                self.task_failed.emit(on_error, str(e))

        asyncio.run_coroutine_threadsafe(runner(), self.loop)


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

class InstallFromLinkDialog(QDialog):
    def __init__(self, parent=None, nexus_api: Optional[NexusAPI] = None, dispatcher: Optional[AsyncDispatcher] = None, initial_url: str = ""):
        super().__init__(parent)
        self.setWindowTitle("Install Mod from Link or URL")
        self.resize(650, 480)
        self.setStyleSheet(DARK_STYLE)
        self.nexus_api = nexus_api or NexusAPI()
        self.dispatcher = dispatcher
        self.parsed_data = None
        self.mod_details = None
        self.mod_files = []

        self._build_ui()
        if initial_url:
            self.input_url.setText(initial_url)
            self._on_check_link()

    def _build_ui(self):
        layout = QVBoxLayout(self)
        layout.setContentsMargins(20, 20, 20, 20)
        layout.setSpacing(14)

        # Header
        title_box = QVBoxLayout()
        title_lbl = QLabel("🌐 Install Mod from Link / URL")
        title_lbl.setStyleSheet("font-weight: bold; font-size: 16px; color: #DA7C21;")
        sub_lbl = QLabel("Paste any Nexus Mods URL, Mod ID, nxm:// protocol link, or direct .zip download link.")
        sub_lbl.setStyleSheet("color: #8F94A6; font-size: 12px;")
        title_box.addWidget(title_lbl)
        title_box.addWidget(sub_lbl)
        layout.addLayout(title_box)

        # Input Row
        input_row = QHBoxLayout()
        self.input_url = QLineEdit()
        self.input_url.setPlaceholderText("e.g. https://www.nexusmods.com/stardewvalley/mods/1915 or 1915 or direct .zip link...")
        self.input_url.returnPressed.connect(self._on_check_link)
        input_row.addWidget(self.input_url)

        self.btn_paste = QPushButton("📋 Paste")
        self.btn_paste.setObjectName("SecondaryBtn")
        self.btn_paste.clicked.connect(self._on_paste_clicked)
        input_row.addWidget(self.btn_paste)

        self.btn_check = QPushButton("🔍 Check")
        self.btn_check.setObjectName("SecondaryBtn")
        self.btn_check.clicked.connect(self._on_check_link)
        input_row.addWidget(self.btn_check)

        layout.addLayout(input_row)

        # Info Frame / Card
        self.card = QFrame()
        self.card.setStyleSheet("background-color: #21242D; border: 1px solid #313543; border-radius: 6px; padding: 14px;")
        card_layout = QVBoxLayout(self.card)
        card_layout.setSpacing(10)

        self.lbl_title = QLabel("Ready to inspect link")
        self.lbl_title.setStyleSheet("font-weight: bold; font-size: 14px; color: #FFFFFF;")
        card_layout.addWidget(self.lbl_title)

        self.lbl_author = QLabel("")
        self.lbl_author.setStyleSheet("color: #DA7C21; font-size: 12px; font-weight: bold;")
        card_layout.addWidget(self.lbl_author)

        self.lbl_desc = QLabel("Enter or paste a mod link or numeric Mod ID above, then click 'Check'.")
        self.lbl_desc.setWordWrap(True)
        self.lbl_desc.setStyleSheet("color: #9A9EAB; font-size: 12px;")
        card_layout.addWidget(self.lbl_desc)

        # File selector dropdown
        self.combo_box_layout = QVBoxLayout()
        self.combo_label = QLabel("Select File to Install:")
        self.combo_label.setStyleSheet("color: #E2E4E9; font-weight: bold; font-size: 12px;")
        self.combo_files = QComboBox()
        self.combo_box_layout.addWidget(self.combo_label)
        self.combo_box_layout.addWidget(self.combo_files)
        self.combo_label.setVisible(False)
        self.combo_files.setVisible(False)
        card_layout.addLayout(self.combo_box_layout)

        self.lbl_status = QLabel("")
        self.lbl_status.setStyleSheet("color: #F1C40F; font-size: 11px;")
        card_layout.addWidget(self.lbl_status)

        layout.addWidget(self.card)
        layout.addStretch()

        # Action Buttons
        btn_row = QHBoxLayout()
        self.btn_cancel = QPushButton("Cancel")
        self.btn_cancel.setObjectName("SecondaryBtn")
        self.btn_cancel.clicked.connect(self.reject)
        btn_row.addWidget(self.btn_cancel)

        btn_row.addStretch()

        self.btn_open_browser = QPushButton("🚀 Open Mod Page & Download")
        self.btn_open_browser.setObjectName("SecondaryBtn")
        self.btn_open_browser.setVisible(False)
        self.btn_open_browser.clicked.connect(self._on_open_browser_clicked)
        btn_row.addWidget(self.btn_open_browser)

        self.btn_install = QPushButton("📥 Download & Install to iPhone")
        self.btn_install.setEnabled(False)
        self.btn_install.clicked.connect(self._on_install_clicked)
        btn_row.addWidget(self.btn_install)

        layout.addLayout(btn_row)

    def _on_paste_clicked(self):
        text = QApplication.clipboard().text().strip()
        if text:
            self.input_url.setText(text)
            self._on_check_link()

    def _on_check_link(self):
        raw = self.input_url.text().strip()
        if not raw:
            self.lbl_title.setText("Please enter a link or Mod ID")
            self.lbl_author.setText("")
            self.lbl_desc.setText("Enter or paste a mod link or numeric Mod ID above.")
            self.combo_label.setVisible(False)
            self.combo_files.setVisible(False)
            self.btn_open_browser.setVisible(False)
            self.btn_install.setEnabled(False)
            return

        parsed = parse_any_url(raw)
        self.parsed_data = parsed
        link_type = parsed.get("type")

        if link_type == "invalid":
            self.lbl_title.setText("❌ Unrecognized Link Format")
            self.lbl_author.setText("")
            self.lbl_desc.setText("Supported formats:\n• Nexus URL: https://www.nexusmods.com/stardewvalley/mods/1915\n• Mod ID: 1915\n• Nexus NXM: nxm://stardewvalley/mods/1915/files/...\n• Direct URL: https://example.com/mod.zip")
            self.combo_label.setVisible(False)
            self.combo_files.setVisible(False)
            self.btn_open_browser.setVisible(False)
            self.btn_install.setEnabled(False)

        elif link_type == "nxm":
            self.lbl_title.setText("🔗 Nexus One-Click Link (NXM Protocol)")
            self.lbl_author.setText(f"Game: {parsed.get('game', 'stardewvalley')} | Mod #{parsed.get('mod_id')} | File #{parsed.get('file_id')}")
            self.lbl_desc.setText("Validated security tokens detected. Ready to download from Nexus CDN and install directly to your iPhone.")
            self.combo_label.setVisible(False)
            self.combo_files.setVisible(False)
            self.btn_open_browser.setVisible(False)
            self.btn_install.setEnabled(True)
            self.btn_install.setText("📥 Download & Install to iPhone")

        elif link_type == "direct_url":
            url = parsed["url"]
            filename = os.path.basename(urllib.parse.urlparse(url).path) or "download.zip"
            self.lbl_title.setText("📦 Direct Download Archive")
            self.lbl_author.setText(f"Target: {filename}")
            self.lbl_desc.setText(f"Direct link: {url}\nReady to download archive and extract mods directly to iPhone.")
            self.combo_label.setVisible(False)
            self.combo_files.setVisible(False)
            self.btn_open_browser.setVisible(False)
            self.btn_install.setEnabled(True)
            self.btn_install.setText("📥 Download & Install to iPhone")

        elif link_type == "nexus_web":
            mod_id = parsed["mod_id"]
            game = parsed.get("game", "stardewvalley")

            if not self.nexus_api.api_key:
                self.lbl_title.setText(f"🔑 API Key Required for Mod #{mod_id}")
                self.lbl_author.setText("")
                self.lbl_desc.setText("To query Nexus Mods details, please enter your Personal API Key in the 'Nexus Downloads' tab.")
                self.btn_open_browser.setVisible(True)
                self.btn_install.setEnabled(False)
                return

            self.lbl_title.setText(f"🔍 Contacting Nexus Mods for Mod #{mod_id}...")
            self.lbl_author.setText("")
            self.lbl_desc.setText("Fetching mod details and downloadable files...")
            self.lbl_status.setText("Connecting...")
            self.btn_install.setEnabled(False)

            def fetch():
                details = self.nexus_api.get_mod_details(game, mod_id)
                files = self.nexus_api.get_mod_files(game, mod_id)
                return details, files

            if self.dispatcher:
                self.dispatcher.run_async(
                    asyncio.to_thread(fetch),
                    on_success=self._on_nexus_details_loaded,
                    on_error=self._on_nexus_details_error
                )
            else:
                try:
                    d, f = fetch()
                    self._on_nexus_details_loaded((d, f))
                except Exception as e:
                    self._on_nexus_details_error(str(e))

    def _on_nexus_details_loaded(self, res):
        details, files = res
        self.mod_details = details
        self.mod_files = files
        self.lbl_status.setText("")

        name = details.get("name", "Unknown Mod")
        author = details.get("author", "Unknown")
        ver = details.get("version", "")
        summary = details.get("summary", "")

        self.lbl_title.setText(f"🎮 {name}")
        self.lbl_author.setText(f"Author: {author}  •  Version: {ver}")
        self.lbl_desc.setText(summary or "No summary provided.")

        # Filter relevant files: MAIN, UPDATE, OPTIONAL
        relevant = [f for f in files if f.get("category_name") in ["MAIN", "UPDATE", "OPTIONAL"]]
        if not relevant:
            relevant = files[:10]

        # Sort: newest timestamp first
        relevant.sort(key=lambda x: x.get("uploaded_timestamp", 0), reverse=True)

        self.combo_files.clear()
        for f in relevant:
            f_id = f.get("file_id")
            f_name = f.get("name", "File")
            f_ver = f.get("version", "")
            f_cat = f.get("category_name", "FILE")
            f_kb = f.get("size_kb", 0)
            f_mb = f_kb / 1024
            label = f"[{f_cat}] {f_name} (v{f_ver}, {f_mb:.1f} MB)"
            self.combo_files.addItem(label, f_id)

        target_fid = self.parsed_data.get("file_id")
        if target_fid:
            idx = self.combo_files.findData(target_fid)
            if idx >= 0:
                self.combo_files.setCurrentIndex(idx)

        self.combo_label.setVisible(True)
        self.combo_files.setVisible(True)
        self.btn_open_browser.setVisible(True)
        self.btn_install.setEnabled(True)
        self.btn_install.setText("📥 Download & Install to iPhone")

    def _on_nexus_details_error(self, err_msg: str):
        self.lbl_status.setText("")
        self.lbl_title.setText("❌ Failed to Query Nexus")
        self.lbl_author.setText("")
        self.lbl_desc.setText(f"Error fetching mod information:\n{err_msg}")
        self.btn_open_browser.setVisible(True)
        self.btn_install.setEnabled(False)

    def _on_open_browser_clicked(self):
        if not self.parsed_data:
            return
        game = self.parsed_data.get("game", "stardewvalley")
        mod_id = self.parsed_data.get("mod_id")
        file_id = self.combo_files.currentData() if self.combo_files.count() > 0 else self.parsed_data.get("file_id")
        if file_id:
            url = f"https://www.nexusmods.com/{game}/mods/{mod_id}?tab=files&file_id={file_id}"
        else:
            url = f"https://www.nexusmods.com/{game}/mods/{mod_id}?tab=files"
        webbrowser.open(url)
        self.accept()

    def _on_install_clicked(self):
        if not self.parsed_data:
            return

        link_type = self.parsed_data.get("type")

        if link_type == "nxm":
            url = self.parsed_data.get("url")
            self.accept()
            if self.parent():
                self.parent()._handle_nxm_url(url)

        elif link_type == "direct_url":
            url = self.parsed_data.get("url")
            self.accept()
            if self.parent():
                self.parent()._handle_direct_download_url(url)

        elif link_type == "nexus_web":
            game = self.parsed_data.get("game", "stardewvalley")
            mod_id = self.parsed_data.get("mod_id")
            file_id = self.combo_files.currentData() if self.combo_files.count() > 0 else self.parsed_data.get("file_id")

            if not file_id:
                QMessageBox.warning(self, "No File", "Please select a file to download.")
                return

            self.btn_install.setEnabled(False)
            self.btn_install.setText("Resolving download link...")
            self.lbl_status.setText("Checking Nexus CDN download link permissions...")

            def try_resolve():
                return self.nexus_api.get_download_links(game, mod_id, file_id)

            def on_success(links):
                self.accept()
                if self.parent():
                    parsed_equiv = {
                        "game": game,
                        "mod_id": mod_id,
                        "file_id": file_id
                    }
                    self.parent()._on_links_resolved(links, parsed_equiv)

            def on_error(err):
                self.btn_install.setEnabled(True)
                self.btn_install.setText("📥 Download & Install to iPhone")
                self.lbl_status.setText("")
                web_url = f"https://www.nexusmods.com/{game}/mods/{mod_id}?tab=files&file_id={file_id}"
                reply = QMessageBox.information(
                    self,
                    "Nexus Authorization Required",
                    f"Nexus Mods requires free/standard accounts to initiate downloads from their website.\n\n"
                    f"Would you like to open the download page now?\n\n"
                    f"Simply click 'MOD MANAGER DOWNLOAD' on the page, and this tool will automatically capture the download and install it straight to your iPhone!",
                    QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No
                )
                if reply == QMessageBox.StandardButton.Yes:
                    webbrowser.open(web_url)
                    self.accept()

            if self.dispatcher:
                self.dispatcher.run_async(
                    asyncio.to_thread(try_resolve),
                    on_success=on_success,
                    on_error=on_error
                )
            else:
                try:
                    links = try_resolve()
                    on_success(links)
                except Exception as e:
                    on_error(str(e))


class ModManagerWindow(QMainWindow):
    nxm_received = pyqtSignal(str)


    def __init__(self, initial_nxm: Optional[str] = None):
        super().__init__()
        self.setWindowTitle("Stardew Valley iOS Mod Manager — Vortex Edition")
        self.resize(1120, 750)
        self.setStyleSheet(DARK_STYLE)
        self.setAcceptDrops(True)

        self.dispatcher = AsyncDispatcher()
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

        self.btn_install_url = QPushButton("🌐 Install from Link / URL")
        self.btn_install_url.clicked.connect(lambda: self._show_install_url_dialog())
        toolbar.addWidget(self.btn_install_url)

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
        self.dispatcher.run_async(
            self.backend.install_mod_archive(next_path),
            on_success=lambda res: self._batch_install_paths(remaining),
            on_error=lambda err: self._batch_install_paths(remaining)
        )

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

        man_title = QLabel("📥 Mod Link / URL Downloader")
        man_title.setStyleSheet("font-weight: bold; font-size: 14px; color: #DA7C21;")
        man_layout.addWidget(man_title)

        man_desc = QLabel("Enter any Nexus link (web page or nxm://), Mod ID number, or direct .zip URL to download and install straight to iPhone:")
        man_desc.setStyleSheet("color: #9A9EAB; font-size: 12px;")
        man_layout.addWidget(man_desc)

        man_row = QHBoxLayout()
        self.nxm_input = QLineEdit()
        self.nxm_input.setPlaceholderText("Paste Nexus link, Mod ID, nxm://, or direct .zip URL...")
        self.nxm_input.returnPressed.connect(lambda: self._handle_any_link_input(self.nxm_input.text().strip()))
        man_row.addWidget(self.nxm_input)

        self.btn_download_nxm = QPushButton("Download & Install")
        self.btn_download_nxm.clicked.connect(lambda: self._handle_any_link_input(self.nxm_input.text().strip()))
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
            return self.nexus_api.validate_api_key()

        self.dispatcher.run_async(
            asyncio.to_thread(check),
            on_success=self._on_key_validated,
            on_error=lambda e: self.lbl_nexus_user.setText(f"Validation error: {e}")
        )

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
            self.config["is_premium"] = is_prem
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

        self.dispatcher.run_async(
            asyncio.to_thread(fetch_links),
            on_success=lambda links: self._on_links_resolved(links, parsed),
            on_error=self._on_nxm_failed
        )

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

    def _show_install_url_dialog(self, initial_url: str = ""):
        dlg = InstallFromLinkDialog(
            parent=self,
            nexus_api=self.nexus_api,
            dispatcher=self.dispatcher,
            initial_url=initial_url
        )
        dlg.exec()

    def _handle_any_link_input(self, raw: str):
        raw = raw.strip()
        if not raw:
            self._show_install_url_dialog()
            return

        parsed = parse_any_url(raw)
        if parsed.get("type") == "nxm":
            self._handle_nxm_url(raw)
        elif parsed.get("type") == "direct_url":
            self._handle_direct_download_url(parsed["url"])
        else:
            self._show_install_url_dialog(raw)

    def _handle_direct_download_url(self, url: str):
        if not self.backend.is_connected:
            QMessageBox.warning(self, "Not Connected", "Please connect your iOS device via USB or select a local folder first.")
            return

        self.progress_bar.setVisible(True)
        self.progress_bar.setRange(0, 100)
        self.progress_bar.setValue(0)
        fname = os.path.basename(urllib.parse.urlparse(url).path) or "archive"
        self.status_bar.setText(f" Downloading {fname}...")

        dl_worker = DownloadWorker(self.nexus_api, url, DEFAULT_DOWNLOAD_DIR)
        dl_worker.progress.connect(self._on_download_progress)
        dl_worker.finished.connect(self._on_download_finished)
        dl_worker.failed.connect(self._on_direct_download_failed)
        self._current_dl_worker = dl_worker
        dl_worker.start()

    def _on_direct_download_failed(self, err: str):
        self.progress_bar.setVisible(False)
        QMessageBox.critical(self, "Download Error", f"Failed to download mod from URL:\n{err}")


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
        self.dispatcher.run_async(
            self.backend.get_smapi_log(),
            on_success=lambda text: self.log_viewer.setPlainText(text),
            on_error=lambda e: self.log_viewer.setPlainText(f"Error fetching log: {e}")
        )

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
        self.dispatcher.run_async(
            self.backend.list_saves(),
            on_success=self._render_saves,
            on_error=lambda e: self.status_bar.setText(f" Error listing saves: {e}")
        )

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
        self.dispatcher.run_async(
            self.backend.backup_save(save_name, dest_dir),
            on_success=lambda res: QMessageBox.information(self, "Backup Complete", res[1]),
            on_error=lambda err: QMessageBox.warning(self, "Backup Error", f"Backup failed: {err}")
        )

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

        self.dispatcher.run_async(
            self.backend.connect_usb(),
            on_success=self._on_usb_connected,
            on_error=self._on_usb_failed
        )

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
        self.dispatcher.run_async(
            self.backend.list_mods(),
            on_success=self._render_mods,
            on_error=lambda e: self.status_bar.setText(f" Error fetching mods: {e}")
        )

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
        self.dispatcher.run_async(
            self.backend.toggle_mod(mod),
            on_success=lambda res: self._refresh_mods(),
            on_error=lambda err: QMessageBox.warning(self, "Error", f"Failed to toggle mod: {err}")
        )

    def _delete_mod(self, mod: ModInfo):
        reply = QMessageBox.question(
            self, "Confirm Delete",
            f"Are you sure you want to permanently delete mod '{mod.name}' from your iOS device?",
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No
        )
        if reply == QMessageBox.StandardButton.Yes:
            self.status_bar.setText(f" Deleting {mod.name}...")
            self.dispatcher.run_async(
                self.backend.delete_mod(mod),
                on_success=lambda res: self._refresh_mods(),
                on_error=lambda err: QMessageBox.warning(self, "Error", f"Failed to delete mod: {err}")
            )

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

        self.dispatcher.run_async(
            self.backend.install_mod_archive(path, lambda msg: self.status_bar.setText(f" {msg}")),
            on_success=self._on_install_finished,
            on_error=self._on_install_failed
        )

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
        if event.mimeData().hasUrls() or event.mimeData().hasText():
            event.acceptProposedAction()

    def dropEvent(self, event: QDropEvent):
        if event.mimeData().hasUrls():
            urls = event.mimeData().urls()
            if urls:
                path = urls[0].toLocalFile()
                if path and os.path.exists(path):
                    self._install_mod(path)
                    return
                raw_url = urls[0].toString()
                if raw_url.startswith(("http://", "https://", "nxm://")):
                    self._handle_any_link_input(raw_url)
                    return
        if event.mimeData().hasText():
            text = event.mimeData().text().strip()
            if text:
                self._handle_any_link_input(text)


    def closeEvent(self, event):
        if hasattr(self, "dispatcher") and self.dispatcher:
            self.dispatcher.loop.call_soon_threadsafe(self.dispatcher.loop.stop)
        event.accept()


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

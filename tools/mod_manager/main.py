"""
Stardew Valley iOS Mod Manager (Vortex-style GUI)
A modern desktop interface for managing mods, saves, and SMAPI logs on iOS devices.
"""

import os
import sys
import asyncio
from typing import Optional
from PyQt6.QtWidgets import (
    QApplication, QMainWindow, QWidget, QVBoxLayout, QHBoxLayout,
    QLabel, QPushButton, QTabWidget, QTableWidget, QTableWidgetItem,
    QHeaderView, QFileDialog, QMessageBox, QLineEdit, QTextEdit,
    QProgressBar, QFrame, QSplitter, QCheckBox, QAbstractItemView
)
from PyQt6.QtCore import Qt, QThread, pyqtSignal, QTimer
from PyQt6.QtGui import QColor, QFont, QIcon, QDragEnterEvent, QDropEvent

# Import backend from same directory
try:
    from tools.mod_manager.backend import IOSModBackend, ModInfo
except ImportError:
    from backend import IOSModBackend, ModInfo


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
    padding: 10px 22px;
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
    """Executes asynchronous coroutines on a background thread with signals."""
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


class ModManagerWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Stardew Valley iOS Mod Manager — Vortex Edition")
        self.resize(1080, 720)
        self.setStyleSheet(DARK_STYLE)
        self.setAcceptDrops(True)

        self.backend = IOSModBackend()
        self.mods_cache: list[ModInfo] = []

        self._build_ui()
        self._auto_connect_usb()

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

        # Logo / Title
        title_box = QVBoxLayout()
        title_lbl = QLabel("STARDEW VALLEY iOS")
        title_lbl.setStyleSheet("font-weight: 900; font-size: 16px; color: #DA7C21; letter-spacing: 1px;")
        sub_lbl = QLabel("Vortex-Style Mod & Save Manager")
        sub_lbl.setStyleSheet("font-size: 11px; color: #8F94A6;")
        title_box.addWidget(title_lbl)
        title_box.addWidget(sub_lbl)
        h_layout.addLayout(title_box)

        h_layout.addStretch()

        # Device connection status badge
        self.device_badge = QLabel("🔍 Checking for iOS Device...")
        self.device_badge.setStyleSheet("""
            background-color: #2D303E;
            color: #DA7C21;
            padding: 6px 14px;
            border-radius: 14px;
            font-weight: bold;
            font-size: 12px;
        """)
        h_layout.addWidget(self.device_badge)

        # Reconnect button
        self.btn_refresh_dev = QPushButton("⟳ Reconnect USB")
        self.btn_refresh_dev.setObjectName("SecondaryBtn")
        self.btn_refresh_dev.clicked.connect(self._auto_connect_usb)
        h_layout.addWidget(self.btn_refresh_dev)

        # Local folder fallback
        self.btn_local_folder = QPushButton("📁 Local Folder")
        self.btn_local_folder.setObjectName("SecondaryBtn")
        self.btn_local_folder.clicked.connect(self._select_local_folder)
        h_layout.addWidget(self.btn_local_folder)

        main_layout.addWidget(header)

        # Progress bar (hidden by default)
        self.progress_bar = QProgressBar()
        self.progress_bar.setFixedHeight(14)
        self.progress_bar.setVisible(False)
        main_layout.addWidget(self.progress_bar)

        # 2. Main Tabs
        self.tabs = QTabWidget()
        self.tabs.setContentsMargins(16, 12, 16, 12)

        self.tab_mods = QWidget()
        self.tab_logs = QWidget()
        self.tab_saves = QWidget()
        self.tab_info = QWidget()

        self.tabs.addTab(self.tab_mods, "  🎮 MODS  ")
        self.tabs.addTab(self.tab_logs, "  📋 SMAPI LOGS  ")
        self.tabs.addTab(self.tab_saves, "  💾 SAVES & BACKUPS  ")
        self.tabs.addTab(self.tab_info, "  ⚙️ DIAGNOSTICS  ")

        self._setup_mods_tab()
        self._setup_logs_tab()
        self._setup_saves_tab()
        self._setup_info_tab()

        main_layout.addWidget(self.tabs)

        # 3. Status Bar
        self.status_bar = QLabel(" Ready")
        self.status_bar.setStyleSheet("background-color: #191B21; color: #8F94A6; padding: 6px 16px; font-size: 11px;")
        main_layout.addWidget(self.status_bar)

    # ----------------- MODS TAB -----------------
    def _setup_mods_tab(self):
        layout = QVBoxLayout(self.tab_mods)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(12)

        # Action Toolbar
        toolbar = QHBoxLayout()

        self.btn_install_mod = QPushButton("📥 Install Mod (ZIP / Folder)")
        self.btn_install_mod.clicked.connect(self._browse_and_install_mod)
        toolbar.addWidget(self.btn_install_mod)

        self.btn_refresh_mods = QPushButton("⟳ Refresh")
        self.btn_refresh_mods.setObjectName("SecondaryBtn")
        self.btn_refresh_mods.clicked.connect(self._refresh_mods)
        toolbar.addWidget(self.btn_refresh_mods)

        toolbar.addSpacing(16)

        # Filter box
        self.search_box = QLineEdit()
        self.search_box.setPlaceholderText("🔍 Filter installed mods by name, author, or unique ID...")
        self.search_box.textChanged.connect(self._filter_mods)
        toolbar.addWidget(self.search_box)

        layout.addLayout(toolbar)

        # Mod Table
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

        # Bottom info bar
        info_row = QHBoxLayout()
        self.lbl_mod_stats = QLabel("Total: 0 mods")
        self.lbl_mod_stats.setStyleSheet("color: #8F94A6; font-size: 12px;")
        info_row.addWidget(self.lbl_mod_stats)

        info_row.addStretch()

        self.lbl_cp_status = QLabel("Content Patcher: Checking...")
        self.lbl_cp_status.setStyleSheet("color: #F1C40F; font-size: 12px; font-weight: bold;")
        info_row.addWidget(self.lbl_cp_status)

        layout.addLayout(info_row)

    # ----------------- LOGS TAB -----------------
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
        self.log_viewer.setPlaceholderText("Click 'Fetch Latest SMAPI Log' to load logs directly from the iOS device...")
        layout.addWidget(self.log_viewer)

    # ----------------- SAVES TAB -----------------
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

    # ----------------- INFO TAB -----------------
    def _setup_info_tab(self):
        layout = QVBoxLayout(self.tab_info)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(16)

        self.info_text = QTextEdit()
        self.info_text.setReadOnly(True)
        layout.addWidget(self.info_text)

    # ----------------- DEVICE & BACKEND LOGIC -----------------
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

            # Col 0: Checkbox / Switch
            chk = QCheckBox()
            chk.setChecked(mod.is_enabled)
            chk.setText(" Enabled" if mod.is_enabled else " Disabled")
            chk.setStyleSheet(
                "color: #2ECC71; font-weight: bold;" if mod.is_enabled else "color: #7F8C8D;"
            )
            # Capture mod in lambda closure
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

            # Col 4: Type badge
            type_str = "Content Pack" if mod.is_content_pack else ("C# Code Mod" if mod.is_code_mod else "Standard Mod")
            type_item = QTableWidgetItem(type_str)
            type_item.setTextAlignment(Qt.AlignmentFlag.AlignCenter)
            type_item.setForeground(QColor("#DA7C21" if mod.is_content_pack else "#3498DB"))
            self.mod_table.setItem(row, 4, type_item)

            # Col 5: Delete button
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

        # Update stats
        enabled_count = sum(1 for m in self.mods_cache if m.is_enabled)
        self.lbl_mod_stats.setText(f"Total: {len(self.mods_cache)} mods ({enabled_count} enabled, {len(self.mods_cache) - enabled_count} disabled)")

        if has_content_patcher:
            self.lbl_cp_status.setText("✓ Content Patcher: INSTALLED")
            self.lbl_cp_status.setStyleSheet("color: #2ECC71; font-size: 12px; font-weight: bold;")
        else:
            self.lbl_cp_status.setText("⚠ Content Patcher: NOT FOUND (Required for CP packs)")
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
        self.progress_bar.setRange(0, 0) # Indeterminate
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

    # Drag and Drop support
    def dragEnterEvent(self, event: QDragEnterEvent):
        if event.mimeData().hasUrls():
            event.acceptProposedAction()

    def dropEvent(self, event: QDropEvent):
        urls = event.mimeData().urls()
        if urls:
            path = urls[0].toLocalFile()
            if os.path.exists(path):
                self._install_mod(path)

    # ----------------- LOGS -----------------
    def _fetch_smapi_log(self):
        if not self.backend.is_connected:
            QMessageBox.warning(self, "Not Connected", "Please connect your device first.")
            return

        self.status_bar.setText(" Fetching SMAPI log from device...")
        worker = AsyncWorker(self.backend.get_smapi_log())
        worker.finished.connect(self._render_log)
        worker.failed.connect(lambda e: self.log_viewer.setPlainText(f"Error fetching log: {e}"))
        self._current_worker = worker
        worker.start()

    def _render_log(self, text: str):
        self.log_viewer.setPlainText(text)
        self.status_bar.setText(" SMAPI log loaded successfully.")

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

    # ----------------- SAVES -----------------
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

    # ----------------- DIAGNOSTICS -----------------
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

        self.info_text.setPlainText("\n".join(lines))


def main():
    app = QApplication(sys.argv)
    window = ModManagerWindow()
    window.show()
    sys.exit(app.exec())


if __name__ == "__main__":
    main()

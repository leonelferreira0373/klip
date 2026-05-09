from klip.toolbar.toolbar import KlipToolbar, Tool


def test_toolbar_default_tool_is_select(qtbot):
    tb = KlipToolbar()
    qtbot.addWidget(tb)
    assert tb.active_tool == Tool.SELECT


def test_toolbar_change_tool(qtbot):
    tb = KlipToolbar()
    qtbot.addWidget(tb)
    tb.set_active_tool(Tool.RECT)
    assert tb.active_tool == Tool.RECT


def test_toolbar_emits_signal_on_change(qtbot):
    tb = KlipToolbar()
    qtbot.addWidget(tb)
    with qtbot.waitSignal(tb.tool_changed) as blocker:
        tb.set_active_tool(Tool.TEXT)
    assert blocker.args == [Tool.TEXT]

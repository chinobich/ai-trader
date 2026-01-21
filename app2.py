import streamlit as st
import yfinance as yf
import pandas as pd
import pandas_ta as ta
from sklearn.ensemble import RandomForestClassifier
import plotly.graph_objects as go
from plotly.subplots import make_subplots
from datetime import datetime
import time
import json
import os
from linebot.v3.messaging import Configuration, ApiClient, MessagingApi, PushMessageRequest, TextMessage

# --- LINE設定 ---
LINE_TOKEN = st.secrets["LINE_CHANNEL_ACCESS_TOKEN"]
USER_ID = st.secrets["LINE_USER_ID"]

# --- 同期用ファイル設定 ---
STATUS_FILE = "status.json"

def get_system_status():
    if os.path.exists(STATUS_FILE):
        with open(STATUS_FILE, "r") as f:
            return json.load(f)
    return {"active": False, "bank": 1000000, "holding": 0, "history": []}

def save_system_status(status):
    with open(STATUS_FILE, "w") as f:
        json.dump(status, f)

# --- ページ設定 ---
st.set_page_config(page_title="AI Terminal Pro Sync", layout="wide")

# --- 状態の初期化 ---
current_status = get_system_status()

# --- カスタムCSS ---
st.markdown("""
    <style>
    html, body, [data-testid="stAppViewContainer"] { background-color: #000000; color: white; }
    .stMetric { background: rgba(255, 255, 255, 0.05); border-radius: 20px !important; padding: 20px !important; border: 1px solid rgba(255,255,255,0.1); }
    </style>
    """, unsafe_allow_html=True)

# --- ヘッダー & 同期コントロール ---
st.title("⚡️ AI Terminal Pro (Cloud Sync)")

c1, c2, c3 = st.columns([2, 1, 2])
with c1:
    target = st.text_input("SYMBOL", value="9509.T")

with c2:
    # 同期された状態に基づいてボタンを表示
    if not current_status["active"]:
        if st.button("▶ START SYSTEM", use_container_width=True, type="primary"):
            current_status["active"] = True
            save_system_status(current_status)
            st.rerun()
    else:
        if st.button("⏹ STOP SYSTEM", use_container_width=True):
            current_status["active"] = False
            save_system_status(current_status)
            st.rerun()

with c3:
    status_text = "🟢 ONLINE (SYNCED)" if current_status["active"] else "🔴 OFFLINE (SYNCED)"
    st.markdown(f"<div style='text-align:right; font-size:1.2rem;'>{status_text}</div>", unsafe_allow_html=True)

# --- LINE送信関数 ---
def send_line(msg):
    try:
        config = Configuration(access_token=LINE_TOKEN)
        with ApiClient(config) as client:
            api = MessagingApi(client)
            api.push_message(PushMessageRequest(to=USER_ID, messages=[TextMessage(text=msg)]))
    except: pass

# --- メインロジック ---
if current_status["active"]:
    # データ取得
    df = yf.download(target, period="5d", interval="1m", progress=False, auto_adjust=True)
    if isinstance(df.columns, pd.MultiIndex): df.columns = df.columns.get_level_values(0)
    
    # テクニカル指標
    df.ta.macd(append=True)
    df.ta.rsi(append=True)
    bb = ta.bbands(df['Close'], length=20)
    df = pd.concat([df, bb], axis=1)
    
    # AI判定
    feats = ['Close', 'RSI_14']
    d_train = df.dropna().copy()
    d_train['Target'] = (d_train['Close'].shift(-1) > d_train['Close']).astype(int)
    model = RandomForestClassifier(n_estimators=100).fit(d_train[feats].iloc[:-1], d_train['Target'].iloc[:-1])
    pred = model.predict(df[feats].iloc[[-1]])[0]
    price = float(df['Close'].iloc[-1])

    # 売買シミュレーション (状態を同期ファイルに保存)
    if pred == 1 and current_status["holding"] == 0:
        current_status["holding"] = price
        current_status["history"].append({"time": str(datetime.now()), "type": "BUY", "price": price})
        save_system_status(current_status)
        send_line(f"🚀【BUY】{target}\n価格: {price}円")
        
    elif pred == 0 and current_status["holding"] > 0:
        profit = (price - current_status["holding"]) * 100
        current_status["bank"] += profit
        current_status["history"].append({"time": str(datetime.now()), "type": "SELL", "price": price, "profit": profit})
        current_status["holding"] = 0
        save_system_status(current_status)
        send_line(f"🤝【SELL】{target}\n価格: {price}円\n損益: {profit:+.1f}円")

    # 表示
    m1, m2, m3, m4 = st.columns(4)
    m1.metric("PORTFOLIO", f"{current_status['bank']:,.0f} JPY")
    m2.metric("POSITION", f"{current_status['holding']:,.1f}" if current_status['holding'] > 0 else "NONE")
    m3.metric("AI PREDICTION", "💹 BUY" if pred == 1 else "💤 WAIT")
    m4.metric("TRADES", len(current_status["history"]))

    # チャート
    fig = make_subplots(rows=1, cols=1)
    fig.add_trace(go.Candlestick(x=df.index, open=df['Open'], high=df['High'], low=df['Low'], close=df['Close'], name="Price"))
    fig.update_layout(height=500, template="plotly_dark", xaxis_rangeslider_visible=False)
    st.plotly_chart(fig, use_container_width=True, key=f"c_{time.time()}")

    time.sleep(60)
    st.rerun()
else:
    st.info("システム待機中。どのデバイスからでもSTART可能です。")
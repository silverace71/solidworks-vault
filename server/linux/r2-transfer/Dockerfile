FROM python:3.13.8-slim
RUN pip install --no-cache-dir boto3==1.40.74 requests==2.32.5
WORKDIR /app
COPY --chmod=644 service.py /app/service.py
USER 1000:1000
CMD ["python", "-u", "/app/service.py"]
